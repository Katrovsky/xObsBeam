﻿// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using ObsInterop;

namespace xObsBeam;

public class BeamSender
{
  public const int MaxFrameQueueSize = 5;
  public const int DefaultPort = 13629;

  ConcurrentDictionary<string, BeamSenderClient> _clients = new ConcurrentDictionary<string, BeamSenderClient>();
  ConcurrentDictionary<string, NamedPipeServerStream> _pipeServers = new ConcurrentDictionary<string, NamedPipeServerStream>();

  TcpListener _listener = new TcpListener(IPAddress.Loopback, DefaultPort);
  string _pipeName = "";
  CancellationTokenSource _listenCancellationSource = new CancellationTokenSource();
  ArrayPool<byte>? _qoiVideoDataPool;
  int _qoiVideoDataPoolMaxSize = 0;
  Beam.VideoHeader _videoHeader;
  Beam.AudioHeader _audioHeader;
  int _videoDataSize = 0;
  int _audioDataSize = 0;
  int _audioBytesPerSample = 0;

  bool _qoiCompression = false;
  bool _compressionThreadingSync = true;

  public unsafe void SetVideoParameters(video_output_info* info, uint* linesize)
  {
    _videoDataSize = 0;
    // get the plane sizes for the current frame format and size
    uint[] planeSizes = Beam.GetPlaneSizes(info->format, info->height, linesize);
    if (planeSizes.Length == 0) // unsupported format
      return;

    // calculate the total size of the video data by summing the plane sizes
    for (int planeIndex = 0; planeIndex < planeSizes.Length; planeIndex++)
      _videoDataSize += (int)planeSizes[planeIndex];

    // create the BeamVideoData object
    _videoHeader = new Beam.VideoHeader()
    {
      Type = Beam.Type.Video,
      DataSize = _videoDataSize,
      Width = info->width,
      Height = info->height,
      Linesize = new ReadOnlySpan<uint>(linesize, Beam.VideoHeader.MAX_AV_PLANES).ToArray(),
      Fps = info->fps_num,
      Format = info->format,
      Range = info->range,
      Colorspace = info->colorspace,
    };

    // cache settings values
    _qoiCompression = SettingsDialog.QoiCompression;
    _compressionThreadingSync = SettingsDialog.QoiCompressionMainThread;

    // QOI's theoretical max size for BGRA is 5x the size of the original image
    if (_qoiCompression)
    {
      _qoiVideoDataPoolMaxSize = (int)((info->width * info->height * 5) + Qoi.PaddingLength);
      _qoiVideoDataPool = ArrayPool<byte>.Create(_qoiVideoDataPoolMaxSize, MaxFrameQueueSize);
    }
    else
    {
      // allow potential previous compression buffers to be garbage collected
      _qoiVideoDataPool = null;
    }

    if (!_qoiCompression)
    {
      var videoBandwidthMbps = (((Beam.VideoHeader.VideoHeaderDataSize + _videoDataSize) * (info->fps_num / info->fps_den)) / 1024 / 1024) * 8;
      Module.Log($"Video output feed initialized, theoretical uncompressed net bandwidth demand is {videoBandwidthMbps} Mpbs.", ObsLogLevel.Info);
    }
    else
      Module.Log($"Video output feed initialized with QOI compression. Sync to render thread: {_compressionThreadingSync}.", ObsLogLevel.Info);
  }

  public unsafe void SetAudioParameters(audio_output_info* info, uint frames)
  {
    Beam.GetAudioDataSize(info->format, info->speakers, frames, out _audioDataSize, out _audioBytesPerSample);
    _audioHeader = new Beam.AudioHeader()
    {
      Type = Beam.Type.Audio,
      DataSize = _audioDataSize,
      Format = info->format,
      SampleRate = info->samples_per_sec,
      Speakers = info->speakers,
      Frames = frames,
    };
  }

  public bool CanStart
  {
    get => (_videoDataSize > 0) && (_audioDataSize > 0);
  }

  public async void Start(string identifier, IPAddress localAddr, int port = DefaultPort)
  {
    if (_videoDataSize == 0)
      throw new InvalidOperationException("Video data size is unknown. Call SetVideoParameters() before calling Start().");
    if (_audioDataSize == 0)
      throw new InvalidOperationException("Audio data size is unknown. Call SetAudioParameters() before calling Start().");

    _listener = new TcpListener(localAddr, port);
    _listener.Start();

    Module.Log($"Listening on {localAddr}:{port}.", ObsLogLevel.Info);

    if (!_listenCancellationSource.TryReset())
    {
      _listenCancellationSource.Dispose();
      _listenCancellationSource = new CancellationTokenSource();
    }

    var stopwatch = new System.Diagnostics.Stopwatch();
    while (!_listenCancellationSource.Token.IsCancellationRequested)
    {
      try
      {
        Module.Log($"Waiting for new connections on {localAddr}:{port}.", ObsLogLevel.Debug);
        stopwatch.Restart();
        var clientSocket = await _listener.AcceptSocketAsync(_listenCancellationSource.Token);
        stopwatch.Stop();
        if (_listenCancellationSource.Token.IsCancellationRequested)
          break;
        if ((clientSocket != null) && (clientSocket.RemoteEndPoint != null))
        {
          Module.Log($"Accept after {stopwatch.ElapsedMilliseconds} ms! Pending: {_listener.Pending()}, Data available: {clientSocket.Available}, Blocking: {clientSocket.Blocking}, Connected: {clientSocket.Connected}, DontFragment: {clientSocket.DontFragment}, Dual: {clientSocket.DualMode}, Broadcast: {clientSocket.EnableBroadcast}, Excl: {clientSocket.ExclusiveAddressUse}, Bound: {clientSocket.IsBound}, Linger: {clientSocket.LingerState!.ToString()}, NoDelay: {clientSocket.NoDelay}, RecvBuff: {clientSocket.ReceiveBufferSize}, SendBuff: {clientSocket.SendBufferSize}, TTL: {clientSocket.Ttl}", ObsLogLevel.Debug);
          string clientId = clientSocket.RemoteEndPoint.ToString()!;
          var client = new BeamSenderClient(clientId, clientSocket, _videoHeader, _audioHeader);
          client.Disconnected += ClientDisconnectedEventHandler;
          client.Start();
          _clients.AddOrUpdate(clientId, client, (key, oldClient) => client);
        }
        else
          Module.Log($"Listener accepted socket but it was null.", ObsLogLevel.Warning);
      }
      catch (OperationCanceledException ex)
      {
        Module.Log($"Listener loop cancelled through {ex.GetType().Name}.", ObsLogLevel.Debug);
        break;
      }
      catch (System.Exception ex)
      {
        Module.Log($"{ex.GetType().Name} in BeamSender.Start: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        throw;
      }
    }
    _listener.Stop();
    Module.Log($"Listener stopped.", ObsLogLevel.Info);
  }

  public async void Start(string identifier, string pipeName)
  {
    if (_videoDataSize == 0)
      throw new InvalidOperationException("Video data size is unknown. Call SetVideoParameters() before calling Start().");
    if (_audioDataSize == 0)
      throw new InvalidOperationException("Audio data size is unknown. Call SetAudioParameters() before calling Start().");

    _pipeName = pipeName;

    var pipeStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);

    Module.Log($"Listening on {_pipeName}.", ObsLogLevel.Info);

    if (!_listenCancellationSource.TryReset())
    {
      _listenCancellationSource.Dispose();
      _listenCancellationSource = new CancellationTokenSource();
    }

    var stopwatch = new System.Diagnostics.Stopwatch();
    while (!_listenCancellationSource.Token.IsCancellationRequested)
    {
      try
      {
        Module.Log($"Waiting for new connections on {_pipeName}.", ObsLogLevel.Debug);
        stopwatch.Restart();
        await pipeStream.WaitForConnectionAsync(_listenCancellationSource.Token);
        stopwatch.Stop();

        if (_listenCancellationSource.Token.IsCancellationRequested)
          break;

        // generate GUID for the client
        string clientId = System.Guid.NewGuid().ToString();
        Module.Log($"Accept after {stopwatch.ElapsedMilliseconds} ms!", ObsLogLevel.Debug);

        // create a new BeamSenderClient
        var client = new BeamSenderClient(clientId, pipeStream, _videoHeader, _audioHeader);
        client.Disconnected += ClientDisconnectedEventHandler;
        client.Start();
        _clients.AddOrUpdate(clientId, client, (key, oldClient) => client);

        // create a new pipe for the next client
        pipeStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);

      }
      catch (OperationCanceledException ex)
      {
        Module.Log($"Listener loop cancelled through {ex.GetType().Name}.", ObsLogLevel.Debug);
        break;
      }
      catch (System.Exception ex)
      {
        Module.Log($"{ex.GetType().Name} in BeamSender.Start: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
        throw;
      }
    }
    pipeStream.Close();
    pipeStream.Dispose();
    Module.Log($"Listener stopped.", ObsLogLevel.Info);
  }

  public void Stop()
  {
    try
    {
      Module.Log($"Stopping BeamSender...", ObsLogLevel.Debug);
      _listenCancellationSource.Cancel();
      foreach (var client in _clients.Values)
        client.Disconnect();
      _videoDataSize = 0;
      _audioDataSize = 0;
      _qoiVideoDataPool = null;

      Module.Log($"Stopped BeamSender.", ObsLogLevel.Debug);
    }
    catch (System.Exception ex)
    {
      Module.Log($"{ex.GetType().Name} in BeamReceiver.Stop: {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
    }
  }

  public unsafe void SendVideo(ulong timestamp, byte* data)
  {
    if (_clients.Count == 0)
      return;


    if (_qoiCompression)
    {
      //TODO: QOI: POC: optionally apply another compression algorithm on top of QOI at the cost of more CPU load, should give good results at least for reducing bandwidth as discussed here: https://github.com/phoboslab/qoi/issues/166
      void sendQoiCompressed(byte* compressionData)
      {
        var encodedData = _qoiVideoDataPool!.Rent(_qoiVideoDataPoolMaxSize);
        try
        {
          int encodedDataLength = 0;
          var frameHeader = _videoHeader;
          encodedDataLength = Qoi.Encode(compressionData, 0, _videoHeader.DataSize, 4, encodedData, true); // encode the frame with QOI
          if (encodedDataLength >= _videoDataSize) // send uncompressed data if compressed would be bigger
          {
            Module.Log($"QOI: sending raw data, since QOI didn't reduce the size.", ObsLogLevel.Debug);
            foreach (var client in _clients.Values)
              client.Enqueue(timestamp, frameHeader, compressionData);
            return;
          }
          frameHeader.Compression = Beam.CompressionTypes.Qoi;
          frameHeader.DataSize = encodedDataLength;
          foreach (var client in _clients.Values)
            client.Enqueue(timestamp, frameHeader, encodedData);
        }
        catch (System.Exception ex)
        {
          Module.Log($"{ex.GetType().Name} in sendCompressed(): {ex.Message}\n{ex.StackTrace}", ObsLogLevel.Error);
          throw;
        }
        finally
        {
          // return the rented encoding data buffer to the pool, each client has created a copy of that data for its own use
          _qoiVideoDataPool!.Return(encodedData);
        }
      }

      if (_compressionThreadingSync)
        sendQoiCompressed(data); // in sync with this main thread, hence the unmanaged data array stays valid and can directly be used
      else
      {
        // not in sync with this main thread, need a copy of the unmanaged data array
        var managedDataCopy = new ReadOnlySpan<byte>(data, _videoDataSize).ToArray();
        Task.Run(() =>
        {
          var localManagedDataCopy = managedDataCopy; // move the data copy to the thread-local context so that it isn't lost when the main thread returns
          fixed (byte* dataCopy = managedDataCopy)
            sendQoiCompressed(dataCopy); // in sync with this main thread, hence the unmanaged data array stays valid and can directly be used
        });
      }
      return;
    }
    else
    {
      var frameHeader = _videoHeader;
      foreach (var client in _clients.Values)
        client.Enqueue(timestamp, frameHeader, data);
    }
  }

  public unsafe void SendAudio(ulong timestamp, int speakers, byte* data)
  {
    // send the audio data to all currently connected clients
    foreach (var client in _clients.Values)
      client.Enqueue(timestamp, data, speakers, _audioBytesPerSample);
  }

  #region Event handlers
  private unsafe void ClientDisconnectedEventHandler(object? sender, EventArgs e)
  {
    string clientId = ((BeamSenderClient)sender!).ClientId;
    if (!_clients.TryRemove(clientId, out _))
      Module.Log($"BeamSender.ClientDisconnectedEventHandler: Could not remove client from dictionary.", ObsLogLevel.Error);
    if (_pipeServers.TryRemove(clientId, out NamedPipeServerStream? pipeServer))
      pipeServer.Dispose();
  }
  #endregion Event handlers

}
