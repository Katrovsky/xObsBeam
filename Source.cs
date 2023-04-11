﻿// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ObsInterop;

namespace xObsBeam;

public class Source
{
  public unsafe struct Context
  {
    public uint SourceId;
    public obs_data* Settings;
    public obs_source* Source;
    public obs_source_frame* Video;
    public obs_source_audio* Audio;
  }

  #region Class fields
  static uint _sourceCount = 0;
  static ConcurrentDictionary<uint, Source> _sourceList = new ConcurrentDictionary<uint, Source>();
  #endregion Class fields

  #region Instance fields
  BeamReceiver BeamReceiver = new BeamReceiver();
  IntPtr ContextPointer;
  uint[] _videoPlaneSizes = new uint[0];
  uint _audioPlaneSize = 0;

  #endregion Instance fields

  #region Helper methods
  public static unsafe void Register()
  {
    var sourceInfo = new obs_source_info();
    fixed (byte* id = "Beam Source"u8)
    {
      sourceInfo.id = (sbyte*)id;
      sourceInfo.type = obs_source_type.OBS_SOURCE_TYPE_INPUT;
      sourceInfo.icon_type = obs_icon_type.OBS_ICON_TYPE_CUSTOM;
      sourceInfo.output_flags = ObsSource.OBS_SOURCE_ASYNC_VIDEO | ObsSource.OBS_SOURCE_AUDIO;
      sourceInfo.get_name = &source_get_name;
      sourceInfo.create = &source_create;
      sourceInfo.get_width = &source_get_width;
      sourceInfo.get_height = &source_get_height;
      sourceInfo.show = &source_show;
      sourceInfo.hide = &source_hide;
      sourceInfo.destroy = &source_destroy;
      sourceInfo.get_defaults = &source_get_defaults;
      sourceInfo.get_properties = &source_get_properties;
      sourceInfo.update = &source_update;
      sourceInfo.save = &source_save;
      ObsSource.obs_register_source_s(&sourceInfo, (nuint)Marshal.SizeOf(sourceInfo));
    }
  }

  private static unsafe Source getSource(void* data)
  {
    var context = (Context*)data;
    return _sourceList[(*context).SourceId];
  }

  private unsafe void connect()
  {
    var context = (Context*)this.ContextPointer;
    var settings = context->Settings;

    fixed (byte*
      propertyTargetHostId = "host"u8,
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPortId = "port"u8,
      propertyConnectionTypePipeId = "connection_type_pipe"u8
    )
    {
      var connectionTypePipe = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypePipeId));
      if (connectionTypePipe)
      {
        string targetPipeName = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyTargetPipeNameId))!;
        if (string.IsNullOrEmpty(targetPipeName))
          targetPipeName = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_default_string(settings, (sbyte*)propertyTargetPipeNameId))!;
        BeamReceiver.Connect(targetPipeName);
      }
      else
      {
        string targetHost = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_string(settings, (sbyte*)propertyTargetHostId))!;
        if (string.IsNullOrEmpty(targetHost) || targetHost == ".")
          targetHost = Marshal.PtrToStringUTF8((IntPtr)ObsData.obs_data_get_default_string(settings, (sbyte*)propertyTargetHostId))!;
        int targetPort = (int)ObsData.obs_data_get_int(settings, (sbyte*)propertyTargetPortId);
        BeamReceiver.Connect(targetHost, targetPort);
      }
    }
  }
  #endregion Helper methods

  #region Source API methods
  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe sbyte* source_get_name(void* data)
  {
    Module.Log("source_get_name called", ObsLogLevel.Debug);
    fixed (byte* sourceName = "Beam"u8)
      return (sbyte*)sourceName;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void* source_create(obs_data* settings, obs_source* source)
  {
    Module.Log("source_create called", ObsLogLevel.Debug);
    Context* context = (Context*)Marshal.AllocCoTaskMem(sizeof(Context));
    context->Settings = settings;
    context->Source = source;
    context->Video = ObsBmem.bzalloc<obs_source_frame>();
    context->Audio = ObsBmem.bzalloc<obs_source_audio>();
    context->SourceId = ++_sourceCount;
    var thisSource = new Source();
    _sourceList.TryAdd(context->SourceId, thisSource);
    thisSource.ContextPointer = (IntPtr)context;
    thisSource.BeamReceiver.VideoFrameReceived += thisSource.VideoFrameReceivedEventHandler;
    thisSource.BeamReceiver.AudioFrameReceived += thisSource.AudioFrameReceivedEventHandler;
    thisSource.BeamReceiver.Disconnected += thisSource.DisconnectedEventHandler;
    return (void*)context;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_destroy(void* data)
  {
    Module.Log("source_destroy called", ObsLogLevel.Debug);
    var context = (Context*)data;
    ObsBmem.bfree(context->Video);
    ObsBmem.bfree(context->Audio);
    Marshal.FreeCoTaskMem((IntPtr)context);
    Module.Log("source_destroy finished", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_show(void* data)
  {
    Module.Log("source_show called", ObsLogLevel.Debug);
    // the activate/deactivate events are not triggered by Studio Mode, so we need to connect/disconnect in show/hide events if the source should also work in Studio Mode
    getSource(data).connect();
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_hide(void* data)
  {
    Module.Log("source_hide called", ObsLogLevel.Debug);
    // the activate/deactivate events are not triggered by Studio Mode, so we need to connect/disconnect in show/hide events if the source should also work in Studio Mode
    getSource(data).BeamReceiver.Disconnect();
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe obs_properties* source_get_properties(void* data)
  {
    Module.Log("source_get_properties called", ObsLogLevel.Debug);

    var properties = ObsProperties.obs_properties_create();
    ObsProperties.obs_properties_set_flags(properties, ObsProperties.OBS_PROPERTIES_DEFER_UPDATE);
    fixed (byte*
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPipeNameCaption = Module.ObsText("TargetPipeNameCaption"),
      propertyTargetPipeNameText = Module.ObsText("TargetPipeNameText"),
      propertyTargetHostId = "host"u8,
      propertyTargetHostCaption = Module.ObsText("TargetHostCaption"),
      propertyTargetHostText = Module.ObsText("TargetHostText"),
      propertyTargetPortId = "port"u8,
      propertyTargetPortCaption = Module.ObsText("TargetPortCaption"),
      propertyTargetPortText = Module.ObsText("TargetPortText"),
      propertyConnectionTypeId = "connection_type"u8,
      propertyConnectionTypeCaption = Module.ObsText("ConnectionTypeCaption"),
      propertyConnectionTypeText = Module.ObsText("ConnectionTypeText"),
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypePipeCaption = Module.ObsText("ConnectionTypePipeCaption"),
      propertyConnectionTypePipeText = Module.ObsText("ConnectionTypePipeText"),
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyConnectionTypeSocketCaption = Module.ObsText("ConnectionTypeSocketCaption"),
      propertyConnectionTypeSocketText = Module.ObsText("ConnectionTypeSocketText")
    )
    {
      // connection type selection group
      var connectionTypePropertyGroup = ObsProperties.obs_properties_create();
      var connectionTypeProperty = ObsProperties.obs_properties_add_group(properties, (sbyte*)propertyConnectionTypeId, (sbyte*)propertyConnectionTypeCaption, obs_group_type.OBS_GROUP_NORMAL, connectionTypePropertyGroup);
      ObsProperties.obs_property_set_long_description(connectionTypeProperty, (sbyte*)propertyConnectionTypeText);
      // connection type pipe option
      var connectionTypePipeProperty = ObsProperties.obs_properties_add_bool(connectionTypePropertyGroup, (sbyte*)propertyConnectionTypePipeId, (sbyte*)propertyConnectionTypePipeCaption);
      ObsProperties.obs_property_set_long_description(connectionTypePipeProperty, (sbyte*)propertyConnectionTypePipeText);
      ObsProperties.obs_property_set_modified_callback(connectionTypePipeProperty, &ConnectionTypePipeChangedEventHandler);
      // connection type socket option
      var connectionTypeSocketProperty = ObsProperties.obs_properties_add_bool(connectionTypePropertyGroup, (sbyte*)propertyConnectionTypeSocketId, (sbyte*)propertyConnectionTypeSocketCaption);
      ObsProperties.obs_property_set_long_description(connectionTypeSocketProperty, (sbyte*)propertyConnectionTypeSocketText);
      ObsProperties.obs_property_set_modified_callback(connectionTypeSocketProperty, &ConnectionTypeSocketChangedEventHandler);

      // target socket/pipe server address
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyTargetHostId, (sbyte*)propertyTargetHostCaption, obs_text_type.OBS_TEXT_DEFAULT), (sbyte*)propertyTargetHostText);

      // target pipe name
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_text(properties, (sbyte*)propertyTargetPipeNameId, (sbyte*)propertyTargetPipeNameCaption, obs_text_type.OBS_TEXT_DEFAULT), (sbyte*)propertyTargetPipeNameText);

      // target socket port
      ObsProperties.obs_property_set_long_description(ObsProperties.obs_properties_add_int(properties, (sbyte*)propertyTargetPortId, (sbyte*)propertyTargetPortCaption, 1024, 65535, 1), (sbyte*)propertyTargetPortText);
    }
    return properties;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_get_defaults(obs_data* settings)
  {
    Module.Log("source_get_defaults called", ObsLogLevel.Debug);
    fixed (byte*
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPipeNameDefaultText = "BeamSender"u8,
      propertyTargetHostId = "host"u8,
      propertyTargetHostDefaultText = "127.0.0.1"u8,
      propertyConnectionTypePipeId = "connection_type_pipe"u8,
      propertyConnectionTypeSocketId = "connection_type_socket"u8,
      propertyTargetPortId = "port"u8
    )
    {
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(true));
      ObsData.obs_data_set_default_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(false));
      ObsData.obs_data_set_default_string(settings, (sbyte*)propertyTargetPipeNameId, (sbyte*)propertyTargetPipeNameDefaultText);
      ObsData.obs_data_set_default_string(settings, (sbyte*)propertyTargetHostId, (sbyte*)propertyTargetHostDefaultText);
      ObsData.obs_data_set_default_int(settings, (sbyte*)propertyTargetPortId, BeamSender.DefaultPort);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_update(void* data, obs_data* settings)
  {
    Module.Log("source_update called", ObsLogLevel.Debug);
    var thisSource = getSource(data);
    if (thisSource.BeamReceiver.IsConnected)
      thisSource.BeamReceiver.Disconnect();
    var context = (Context*)data;
    if (Convert.ToBoolean(Obs.obs_source_showing(context->Source))) // auto-reconnect if the source is visible
      thisSource.connect();
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe void source_save(void* data, obs_data* settings)
  {
    Module.Log("source_save called", ObsLogLevel.Debug);
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe uint source_get_width(void* data)
  {
    return getSource(data).BeamReceiver.Width;
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe uint source_get_height(void* data)
  {
    return getSource(data).BeamReceiver.Height;
  }

  #endregion Source API methods

  #region Event handlers

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte ConnectionTypePipeChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte* propertyConnectionTypePipeId = "connection_type_pipe"u8, propertyConnectionTypeSocketId = "connection_type_socket"u8)
    {
      var connectionTypePipe = Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypePipeId));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyConnectionTypeSocketId, Convert.ToByte(!connectionTypePipe));
      connectionTypeChanged(connectionTypePipe, properties);
      return Convert.ToByte(true);
    }
  }

  [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
  public static unsafe byte ConnectionTypeSocketChangedEventHandler(obs_properties* properties, obs_property* prop, obs_data* settings)
  {
    fixed (byte* propertyConnectionTypePipeId = "connection_type_pipe"u8, propertyConnectionTypeSocketId = "connection_type_socket"u8)
    {
      var connectionTypePipe = !Convert.ToBoolean(ObsData.obs_data_get_bool(settings, (sbyte*)propertyConnectionTypeSocketId));
      ObsData.obs_data_set_bool(settings, (sbyte*)propertyConnectionTypePipeId, Convert.ToByte(connectionTypePipe));
      connectionTypeChanged(connectionTypePipe, properties);
      return Convert.ToByte(true);
    }
  }

  private static unsafe void connectionTypeChanged(bool connectionTypePipe, obs_properties* properties)
  {
    fixed (byte*
      propertyTargetHostId = "host"u8,
      propertyTargetPipeNameId = "pipe_name"u8,
      propertyTargetPortId = "port"u8
    )
    {
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyTargetHostId), Convert.ToByte(!connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyTargetPipeNameId), Convert.ToByte(connectionTypePipe));
      ObsProperties.obs_property_set_visible(ObsProperties.obs_properties_get(properties, (sbyte*)propertyTargetPortId), Convert.ToByte(!connectionTypePipe));
      Module.Log("Connection type changed to: " + (connectionTypePipe ? "pipe" : "socket"), ObsLogLevel.Debug);
    }
  }

  private unsafe void DisconnectedEventHandler(object? sender, EventArgs e)
  {
    var context = (Context*)this.ContextPointer;

    // reset video output
    Obs.obs_source_output_video(context->Source, null);

    // reset audio output
    context->Audio->timestamp = 0;
    context->Audio->frames = 0;
    Obs.obs_source_output_audio(context->Source, context->Audio);

    if (Convert.ToBoolean(Obs.obs_source_showing(context->Source))) // auto-reconnect if the source is visible
    {
      Task.Delay(1000).ContinueWith(_ =>
      {
        if (Convert.ToBoolean(Obs.obs_source_showing(context->Source))) // some time has passed, check again whether the source is still visible
          connect(); // reconnect
      });
    }

  }

  private unsafe Task VideoFrameReceivedEventHandler(object? sender, Beam.BeamVideoData videoFrame)
  {
    var context = (Context*)this.ContextPointer;

    // did the frame format or size change?
    if ((context->Video->width != videoFrame.Header.Width) || (context->Video->height != videoFrame.Header.Height) || (context->Video->format != videoFrame.Header.Format))
    {
      Module.Log($"VideoFrameReceivedEventHandler(): Frame format or size changed, reinitializing ({context->Video->format} {context->Video->width}x{context->Video->height} -> {videoFrame.Header.Format} {videoFrame.Header.Width}x{videoFrame.Header.Height})", ObsLogLevel.Debug);

      // initialize the frame base settings with the new frame format and size
      context->Video->format = videoFrame.Header.Format;
      context->Video->width = videoFrame.Header.Width;
      context->Video->height = videoFrame.Header.Height;
      for (int i = 0; i < Beam.VideoHeader.MAX_AV_PLANES; i++)
        context->Video->linesize[i] = videoFrame.Header.Linesize[i];
      // get the plane sizes for the current frame format and size
      _videoPlaneSizes = Beam.GetPlaneSizes(context->Video->format, context->Video->height, context->Video->linesize);
      if (_videoPlaneSizes.Length == 0) // unsupported format
        return Task.CompletedTask;
      ObsVideo.video_format_get_parameters_for_format(videoFrame.Header.Colorspace, videoFrame.Header.Range, videoFrame.Header.Format, context->Video->color_matrix, context->Video->color_range_min, context->Video->color_range_max);
      Module.Log("VideoFrameReceivedEventHandler(): reinitialized", ObsLogLevel.Debug);
    }

    if (_videoPlaneSizes.Length == 0) // unsupported format
      return Task.CompletedTask;

    context->Video->timestamp = videoFrame.Header.Timestamp;

    fixed (byte* videoData = videoFrame.Data) // temporary pinning is sufficient, since Obs.obs_source_output_video() creates a copy of the data anyway
    {
      // video data in the array is already in the correct order, but the array offsets need to be set correctly according to the plane sizes
      uint currentOffset = 0;
      for (int planeIndex = 0; planeIndex < _videoPlaneSizes.Length; planeIndex++)
      {
        context->Video->data[planeIndex] = videoData + currentOffset;
        currentOffset += _videoPlaneSizes[planeIndex];
      }
      Obs.obs_source_output_video(context->Source, context->Video);
    }
    return Task.CompletedTask;
  }
  private unsafe Task AudioFrameReceivedEventHandler(object? sender, Beam.BeamAudioData audioFrame)
  {
    var context = (Context*)this.ContextPointer;

    // did the frame format or size change?
    if ((context->Audio->samples_per_sec != audioFrame.Header.SampleRate) || (context->Audio->frames != audioFrame.Header.Frames) || (context->Audio->speakers != audioFrame.Header.Speakers) || (context->Audio->format != audioFrame.Header.Format))
    {
      Module.Log($"AudioFrameReceivedEventHandler(): Frame format or size changed, reinitializing ({context->Audio->format} {context->Audio->samples_per_sec} {context->Audio->speakers} {context->Audio->frames} -> {audioFrame.Header.Format} {audioFrame.Header.SampleRate} {audioFrame.Header.Speakers} {audioFrame.Header.Frames})", ObsLogLevel.Debug);

      // initialize the frame base settings with the new frame format and size
      context->Audio->samples_per_sec = audioFrame.Header.SampleRate;
      context->Audio->speakers = audioFrame.Header.Speakers;
      context->Audio->format = audioFrame.Header.Format;
      context->Audio->frames = audioFrame.Header.Frames;
      // calculate the plane size for the current frame format and size
      int audioBytesPerSample;
      Beam.GetAudioDataSize(audioFrame.Header.Format, audioFrame.Header.Speakers, audioFrame.Header.Frames, out _, out audioBytesPerSample);
      uint _audioPlaneSize = (uint)audioBytesPerSample * audioFrame.Header.Frames;
      Module.Log("AudioFrameReceivedEventHandler(): reinitialized", ObsLogLevel.Debug);
    }

    context->Audio->timestamp = audioFrame.Header.Timestamp;

    fixed (byte* audioData = audioFrame.Data) // temporary pinning is sufficient, since Obs.obs_source_output_audio() creates a copy of the data anyway
    {
      // audio data in the array is already in the correct order, but the array offsets need to be set correctly according to the plane sizes
      uint currentOffset = 0;
      for (int speakerIndex = 0; speakerIndex < (int)audioFrame.Header.Speakers; speakerIndex++)
      {
        context->Audio->data[speakerIndex] = audioData + currentOffset;
        currentOffset += _audioPlaneSize;
      }
      Obs.obs_source_output_audio(context->Source, context->Audio);
    }

    return Task.CompletedTask;
  }
  #endregion Event handlers



}