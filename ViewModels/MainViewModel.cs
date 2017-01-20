//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml.Controls;
using PeerConnectionClient.Model;
using PeerConnectionClient.MVVM;
using PeerConnectionClient.Signalling;
using PeerConnectionClient.Utilities;

#if ORTCLIB
using Org.Ortc;
using Org.Ortc.Adapter;
using PeerConnectionClient.Ortc;
using PeerConnectionClient.Ortc.Utilities;
using CodecInfo = Org.Ortc.RTCRtpCodecCapability;
using MediaVideoTrack = Org.Ortc.MediaStreamTrack;
using MediaAudioTrack = Org.Ortc.MediaStreamTrack;
using FrameCounterHelper= PeerConnectionClient.Ortc.OrtcStatsManager;
#else

using Org.WebRtc;

#endif

namespace PeerConnectionClient.ViewModels
{
    public delegate void InitializedDelegate();

    internal class MainViewModel : DispatcherBindableBase
    {
        public event InitializedDelegate OnInitialized;

        /// <summary>
        /// Constructor for MainViewModel.
        /// </summary>
        /// <param name="uiDispatcher">Core event message dispatcher.</param>
        public MainViewModel(CoreDispatcher uiDispatcher)
            : base(uiDispatcher)
        {
            // Initialize all the action commands
            ConnectCommand = new ActionCommand(ConnectCommandExecute, ConnectCommandCanExecute);
            ConnectToPeerCommand = new ActionCommand(ConnectToPeerCommandExecute, ConnectToPeerCommandCanExecute);
            DisconnectFromPeerCommand = new ActionCommand(DisconnectFromPeerCommandExecute, DisconnectFromPeerCommandCanExecute);
            DisconnectFromServerCommand = new ActionCommand(DisconnectFromServerExecute, DisconnectFromServerCanExecute);
            // Configure application version string format
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            AppVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

            IsReadyToConnect = true;
            ScrollBarVisibilityType = ScrollBarVisibility.Auto;

            // Display a permission dialog to request access to the microphone and camera
            WebRTC.RequestAccessForMediaCapture().AsTask().ContinueWith(antecedent =>
            {
                if (antecedent.Result)
                {
                    Initialize(uiDispatcher);
                }
                else
                {
                    RunOnUiThread(async () =>
                    {
                        var msgDialog = new MessageDialog(
                            "Failed to obtain access to multimedia devices!");
                        await msgDialog.ShowAsync();
                    });
                }
            });
        }

        // Help to make sure the screen is not locked while on call
        private readonly DisplayRequest _keepScreenOnRequest = new DisplayRequest();

        private bool _keepOnScreenRequested;

        private MediaVideoTrack _peerVideoTrack;
        private MediaVideoTrack _selfVideoTrack;

        /// <summary>
        /// The initializer for MainViewModel.
        /// </summary>
        /// <param name="uiDispatcher">The UI dispatcher.</param>
        public void Initialize(CoreDispatcher uiDispatcher)
        {
            WebRTC.Initialize(uiDispatcher);
            Cameras = new ObservableCollection<MediaDevice>();
            AudioPlayoutDevices = new ObservableCollection<MediaDevice>();
            Microphones = new ObservableCollection<MediaDevice>();
            // Get information of cameras attached to the device
            foreach (MediaDevice videoCaptureDevice in Conductor.Instance.Media.GetVideoCaptureDevices())
            {
                Cameras.Add(videoCaptureDevice);
            }

            foreach (MediaDevice audioCaptureDevice in Conductor.Instance.Media.GetAudioCaptureDevices())
            {
                Microphones.Add(audioCaptureDevice);
            }

            foreach (MediaDevice audioPlayoutDevice in Conductor.Instance.Media.GetAudioPlayoutDevices())
            {
                AudioPlayoutDevices.Add(audioPlayoutDevice);
            }

            if (SelectedCamera == null && Cameras.Count > 0)
            {
                SelectedCamera = Cameras.First();
            }
            if (SelectedMicrophone == null && Microphones.Count > 0)
            {
                SelectedMicrophone = Microphones.First();
            }
            if (SelectedAudioPlayoutDevice == null && AudioPlayoutDevices.Count > 0)
            {
                SelectedAudioPlayoutDevice = AudioPlayoutDevices.First();
            }
            Conductor.Instance.Media.OnMediaDevicesChanged += OnMediaDevicesChanged;

            // Handler for Peer/Self video frame rate changed event
            FrameCounterHelper.FramesPerSecondChanged += (id, frameRate) =>
            {
                //$debug fpas change
                //RunOnUiThread(() =>
                //{
                //    if (id == "SELF")
                //    {
                //        SelfVideoFps = frameRate;
                //    }
                //    else if (id == "PEER")
                //    {
                //        PeerVideoFps = frameRate;
                //    }
                //});
            };

            // Handler for Peer/Self video resolution changed event
            ResolutionHelper.ResolutionChanged += (id, width, height) =>
            {
                //$debug res change
                //RunOnUiThread(() =>
                //{
                //    if (id == "SELF")
                //    {
                //        SelfHeight = height.ToString();
                //        SelfWidth = width.ToString();
                //    }
                //    else if (id == "PEER")
                //    {
                //        PeerHeight = height.ToString();
                //        PeerWidth = width.ToString();
                //    }
                //});
            };

            // A Peer is connected to the server event handler
            Conductor.Instance.Signaller.OnPeerConnected += (peerId, peerName) =>
            {
                RunOnUiThread(() =>
                {
                    if (Peers == null)
                    {
                        Peers = new ObservableCollection<Peer>();
                        Conductor.Instance.Peers = Peers;
                    }
                    Peers.Add(new Peer { Id = peerId, Name = peerName });
                });
            };

            // A Peer is disconnected from the server event handler
            Conductor.Instance.Signaller.OnPeerDisconnected += peerId =>
            {
                RunOnUiThread(() =>
                {
                    var peerToRemove = Peers?.FirstOrDefault(p => p.Id == peerId);
                    if (peerToRemove != null)
                        Peers.Remove(peerToRemove);
                });
            };

            // The user is Signed in to the server event handler
            Conductor.Instance.Signaller.OnSignedIn += () =>
            {
                RunOnUiThread(() =>
                {
                    IsConnected = true;
                    IsMicrophoneEnabled = true;
                    IsCameraEnabled = true;
                    IsConnecting = false;
                });
            };

            // Failed to connect to the server event handler
            Conductor.Instance.Signaller.OnServerConnectionFailure += () =>
            {
                RunOnUiThread(async () =>
                {
                    IsConnecting = false;
                    MessageDialog msgDialog = new MessageDialog("Failed to connect to server!");
                    await msgDialog.ShowAsync();
                });
            };

            // The current user is disconnected from the server event handler
            Conductor.Instance.Signaller.OnDisconnected += () =>
            {
                RunOnUiThread(() =>
                {
                    IsConnected = false;
                    IsMicrophoneEnabled = false;
                    IsCameraEnabled = false;
                    IsDisconnecting = false;
                    Peers?.Clear();
                });
            };

            // Event handlers for managing the media streams

            Conductor.Instance.OnAddRemoteStream += Conductor_OnAddRemoteStream;
            Conductor.Instance.OnRemoveRemoteStream += Conductor_OnRemoveRemoteStream;
            Conductor.Instance.OnAddLocalStream += Conductor_OnAddLocalStream;

            //PlotlyManager.UpdateUploadingStatsState += PlotlyManager_OnUpdatedUploadingStatsState;
            //PlotlyManager.OnError += PlotlyManager_OnError;
            // Connected to a peer event handler
            Conductor.Instance.OnPeerConnectionCreated += () =>
            {
                RunOnUiThread(() =>
                {
                    IsReadyToConnect = false;
                    IsConnectedToPeer = true;

                    // Make sure the screen is always active while on call
                    if (!_keepOnScreenRequested)
                    {
                        _keepScreenOnRequest.RequestActive();
                        _keepOnScreenRequested = true;
                    }

                    UpdateScrollBarVisibilityTypeHelper();
                });
            };

            // Connection between the current user and a peer is closed event handler
            Conductor.Instance.OnPeerConnectionClosed += () =>
            {
                RunOnUiThread(() =>
                {
                    IsConnectedToPeer = false;
                    _peerVideoTrack = null;
                    _selfVideoTrack = null;
                    GC.Collect(); // Ensure all references are truly dropped.
                    IsMicrophoneEnabled = true;
                    IsCameraEnabled = true;

                    // Make sure to allow the screen to be locked after the call
                    if (_keepOnScreenRequested)
                    {
                        _keepScreenOnRequest.RequestRelease();
                        _keepOnScreenRequested = false;
                    }
                    UpdateScrollBarVisibilityTypeHelper();
                });
            };

            // Ready to connect to the server event handler
            Conductor.Instance.OnReadyToConnect += () => { RunOnUiThread(() => { IsReadyToConnect = true; }); };

            // Initialize the Ice servers list
            IceServers = new ObservableCollection<IceServer>();

            // Prepare to list supported audio codecs
            AudioCodecs = new ObservableCollection<CodecInfo>();
            var audioCodecList = WebRTC.GetAudioCodecs();

            // These are features added to existing codecs, they can't decode/encode real audio data so ignore them
            string[] incompatibleAudioCodecs = new string[] { "CN32000", "CN16000", "CN8000", "red8000", "telephone-event8000" };

            // Prepare to list supported video codecs

            // Load the supported audio/video information into the Settings controls
            RunOnUiThread(() =>
            {
                SelectedVideoCodec = WebRTC.GetVideoCodecs().OrderBy(codec =>
                {
                    switch (codec.Name)
                    {
                        case "VP8": return 1;
                        case "VP9": return 2;
                        case "H264": return 3;
                        default: return 99;
                    }
                }).First();
                foreach (var audioCodec in audioCodecList)
                {
                    if (!incompatibleAudioCodecs.Contains(audioCodec.Name + audioCodec.ClockRate))
                    {
                        AudioCodecs.Add(audioCodec);
                    }
                }

                if (AudioCodecs.Count > 0)
                {
                    SelectedAudioCodec = AudioCodecs.First();
                }
            });
            LoadSettings();
            RunOnUiThread(() =>
            {
                OnInitialized?.Invoke();
            });
        }

        /// <summary>
        /// Handle media devices change event triggered by WebRTC.
        /// </summary>
        /// <param name="mediaType">The type of devices changed</param>
        private void OnMediaDevicesChanged(MediaDeviceType mediaType)
        {
            switch (mediaType)
            {
                case MediaDeviceType.MediaDeviceType_VideoCapture:
                    RefreshVideoCaptureDevices(Conductor.Instance.Media.GetVideoCaptureDevices());
                    break;

                case MediaDeviceType.MediaDeviceType_AudioCapture:
                    RefreshAudioCaptureDevices(Conductor.Instance.Media.GetAudioCaptureDevices());
                    break;

                case MediaDeviceType.MediaDeviceType_AudioPlayout:
                    RefreshAudioPlayoutDevices(Conductor.Instance.Media.GetAudioPlayoutDevices());
                    break;
            }
        }

        /// <summary>
        /// Refresh video capture devices list.
        /// </summary>
        private void RefreshVideoCaptureDevices(IList<MediaDevice> videoCaptureDevices)
        {
            RunOnUiThread(() =>
            {
                Collection<MediaDevice> videoCaptureDevicesToRemove = new Collection<MediaDevice>();
                foreach (MediaDevice videoCaptureDevice in Cameras)
                {
                    if (videoCaptureDevices.FirstOrDefault(x => x.Id == videoCaptureDevice.Id) == null)
                    {
                        videoCaptureDevicesToRemove.Add(videoCaptureDevice);
                    }
                }
                foreach (MediaDevice removedVideoCaptureDevices in videoCaptureDevicesToRemove)
                {
                    if (SelectedCamera != null && SelectedCamera.Id == removedVideoCaptureDevices.Id)
                    {
                        SelectedCamera = null;
                    }
                    Cameras.Remove(removedVideoCaptureDevices);
                }
                foreach (MediaDevice videoCaptureDevice in videoCaptureDevices)
                {
                    if (Cameras.FirstOrDefault(x => x.Id == videoCaptureDevice.Id) == null)
                    {
                        Cameras.Add(videoCaptureDevice);
                    }
                }

                if (SelectedCamera == null)
                {
                    SelectedCamera = Cameras.FirstOrDefault();
                }
            });
        }

        /// <summary>
        /// Refresh audio capture devices list.
        /// </summary>
        private void RefreshAudioCaptureDevices(IList<MediaDevice> audioCaptureDevices)
        {
            RunOnUiThread(() =>
            {
                var selectedMicrophoneId = SelectedMicrophone?.Id;
                SelectedMicrophone = null;
                Microphones.Clear();
                foreach (MediaDevice audioCaptureDevice in audioCaptureDevices)
                {
                    Microphones.Add(audioCaptureDevice);
                    if (audioCaptureDevice.Id == selectedMicrophoneId)
                    {
                        SelectedMicrophone = Microphones.Last();
                    }
                }
                if (SelectedMicrophone == null)
                {
                    SelectedMicrophone = Microphones.FirstOrDefault();
                }

                if (SelectedMicrophone == null)
                {
                    SelectedMicrophone = Microphones.FirstOrDefault();
                }
            });
        }

        /// <summary>
        /// Refresh audio playout devices list.
        /// </summary>
        private void RefreshAudioPlayoutDevices(IList<MediaDevice> audioPlayoutDevices)
        {
            RunOnUiThread(() =>
            {
                var selectedPlayoutDeviceId = SelectedAudioPlayoutDevice?.Id;
                SelectedAudioPlayoutDevice = null;
                AudioPlayoutDevices.Clear();
                foreach (MediaDevice audioPlayoutDevice in audioPlayoutDevices)
                {
                    AudioPlayoutDevices.Add(audioPlayoutDevice);
                    if (audioPlayoutDevice.Id == selectedPlayoutDeviceId)
                    {
                        SelectedAudioPlayoutDevice = audioPlayoutDevice;
                    }
                }
                if (SelectedAudioPlayoutDevice == null)
                {
                    SelectedAudioPlayoutDevice = AudioPlayoutDevices.FirstOrDefault();
                }
            });
        }

        /// <summary>
        /// Add remote stream event handler.
        /// </summary>
        /// <param name="evt">Details about Media stream event.</param>
        private void Conductor_OnAddRemoteStream(MediaStreamEvent evt)
        {
            // $debug Handle incoming remote stream
            //var mediaVideoTracks = evt.Stream.GetVideoTracks();
            // var source = Media.CreateMedia().CreateMediaSource(_peerVideoTrack, "PEER");
            //var rawSource = Media.CreateMedia().CreateRawVideoSource(_peerVideoTrack);
            IsReadyToDisconnect = true;
        }

        /// <summary>
        /// Remove remote stream event handler.
        /// </summary>
        /// <param name="evt">Details about Media stream event.</param>
        private void Conductor_OnRemoveRemoteStream(MediaStreamEvent evt)
        {
            // $debug Handle Stream removed
        }

        /// <summary>
        /// Add local stream event handler.
        /// </summary>
        /// <param name="evt">Details about Media stream event.</param>
        private void Conductor_OnAddLocalStream(MediaStreamEvent evt)
        {
            //local media stream = evt.Stream.GetVideoTracks().FirstOrDefault();
        }

#if PLOTLY
        private void PlotlyManager_OnUpdatedUploadingStatsState(bool uploading)
        {
            IsUploadingStatsInProgress = uploading;
        }

        private void PlotlyManager_OnError(string error)
        {
            RunOnUiThread(async ()=>
            {
                var messageDialog = new MessageDialog(error);

                messageDialog.Commands.Add(new UICommand("Close"));

                messageDialog.DefaultCommandIndex = 0;
                messageDialog.CancelCommandIndex = 1;
                await messageDialog.ShowAsync();
            });
        }
#endif

        #region Bindings

        private ValidableNonEmptyString _ip;

        /// <summary>
        /// IP address of the server to connect.
        /// </summary>
        public ValidableNonEmptyString Ip
        {
            get { return _ip; }
            set
            {
                SetProperty(ref _ip, value);
                _ip.PropertyChanged += Ip_PropertyChanged;
            }
        }

        private ValidableIntegerString _port;

        /// <summary>
        /// The port used to connect to the server.
        /// </summary>
        public ValidableIntegerString Port
        {
            get { return _port; }
            set
            {
                SetProperty(ref _port, value);
                _port.PropertyChanged += Port_PropertyChanged;
            }
        }

        private ObservableCollection<Peer> _peers;

        /// <summary>
        /// The list of connected peers.
        /// </summary>
        public ObservableCollection<Peer> Peers
        {
            get { return _peers; }
            set { SetProperty(ref _peers, value); }
        }

        private Peer _selectedPeer;

        /// <summary>
        /// The selected peer's info.
        /// </summary>
        public Peer SelectedPeer
        {
            get { return _selectedPeer; }
            set
            {
                SetProperty(ref _selectedPeer, value);
                ConnectToPeerCommand.RaiseCanExecuteChanged();
            }
        }

        private ActionCommand _connectCommand;

        /// <summary>
        /// Command to connect to the server.
        /// </summary>
        public ActionCommand ConnectCommand
        {
            get { return _connectCommand; }
            set { SetProperty(ref _connectCommand, value); }
        }

        private ActionCommand _connectToPeerCommand;

        /// <summary>
        /// Command to connect to a peer.
        /// </summary>
        public ActionCommand ConnectToPeerCommand
        {
            get { return _connectToPeerCommand; }
            set { SetProperty(ref _connectToPeerCommand, value); }
        }

        private ActionCommand _disconnectFromPeerCommand;

        /// <summary>
        /// Command to disconnect from a peer.
        /// </summary>
        public ActionCommand DisconnectFromPeerCommand
        {
            get { return _disconnectFromPeerCommand; }
            set { SetProperty(ref _disconnectFromPeerCommand, value); }
        }

        private ActionCommand _disconnectFromServerCommand;

        /// <summary>
        /// Command to disconnect from the server.
        /// </summary>
        public ActionCommand DisconnectFromServerCommand
        {
            get { return _disconnectFromServerCommand; }
            set { SetProperty(ref _disconnectFromServerCommand, value); }
        }

        private ActionCommand _removeSelectedIceServerCommand;

        /// <summary>
        /// Command to remove an Ice server from the list.
        /// </summary>
        public ActionCommand RemoveSelectedIceServerCommand
        {
            get { return _removeSelectedIceServerCommand; }
            set { SetProperty(ref _removeSelectedIceServerCommand, value); }
        }

        private bool _hasServer;

        /// <summary>
        /// Indicator if a server IP address is specified in Settings.
        /// </summary>
        public bool HasServer
        {
            get { return _hasServer; }
            set { SetProperty(ref _hasServer, value); }
        }

        private bool _isConnected;

        /// <summary>
        /// Indicator if the user is connected to the server.
        /// </summary>
        public bool IsConnected
        {
            get { return _isConnected; }
            set
            {
                SetProperty(ref _isConnected, value);
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectFromServerCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _isConnecting;

        /// <summary>
        /// Indicator if the application is in the process of connecting to the server.
        /// </summary>
        public bool IsConnecting
        {
            get { return _isConnecting; }
            set
            {
                SetProperty(ref _isConnecting, value);
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _isDisconnecting;

        /// <summary>
        /// Indicator if the application is in the process of disconnecting from the server.
        /// </summary>
        public bool IsDisconnecting
        {
            get { return _isDisconnecting; }
            set
            {
                SetProperty(ref _isDisconnecting, value);
                DisconnectFromServerCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _isConnectedToPeer;

        /// <summary>
        /// Indicator if the user is connected to a peer.
        /// </summary>
        public bool IsConnectedToPeer
        {
            get { return _isConnectedToPeer; }
            set
            {
                SetProperty(ref _isConnectedToPeer, value);
                ConnectToPeerCommand.RaiseCanExecuteChanged();
                DisconnectFromPeerCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _isReadyToConnect;

        /// <summary>
        /// Indicator if the app is ready to connect to a peer.
        /// </summary>
        public bool IsReadyToConnect
        {
            get { return _isReadyToConnect; }
            set
            {
                SetProperty(ref _isReadyToConnect, value);
                ConnectToPeerCommand.RaiseCanExecuteChanged();
                DisconnectFromPeerCommand.RaiseCanExecuteChanged();
            }
        }

        private bool _isReadyToDisconnect;

        /// <summary>
        /// Indicator if the app is ready to disconnect from a peer.
        /// </summary>
        public bool IsReadyToDisconnect
        {
            get { return _isReadyToDisconnect; }
            set
            {
                SetProperty(ref _isReadyToDisconnect, value);
                DisconnectFromPeerCommand.RaiseCanExecuteChanged();
            }
        }

        private ScrollBarVisibility _scrollBarVisibility;

        /// <summary>
        /// The scroll bar visibility type.
        /// This is used to have a scrollable UI if the application
        /// main page is bigger in size than the device screen.
        /// </summary>
        public ScrollBarVisibility ScrollBarVisibilityType
        {
            get { return _scrollBarVisibility; }
            set { SetProperty(ref _scrollBarVisibility, value); }
        }

        private bool _cameraEnabled = true;

        /// <summary>
        /// Camera on/off toggle button.
        /// Disabled/Enabled local stream if the camera is off/on.
        /// </summary>
        public bool CameraEnabled
        {
            get { return _cameraEnabled; }
            set
            {
                if (!SetProperty(ref _cameraEnabled, value))
                {
                    return;
                }

                if (IsConnectedToPeer)
                {
                    Conductor.Instance.EnableLocalVideoStream();
                }
            }
        }

        private bool _microphoneIsOn = true;

        /// <summary>
        /// Microphone on/off toggle button.
        /// Unmute/Mute audio if the microphone is off/on.
        /// </summary>
        public bool MicrophoneIsOn
        {
            get { return _microphoneIsOn; }
            set
            {
                if (!SetProperty(ref _microphoneIsOn, value))
                {
                    return;
                }

                if (IsConnectedToPeer)
                {
                    Conductor.Instance.UnmuteMicrophone();
                }
            }
        }

        private bool _isMicrophoneEnabled = true;

        /// <summary>
        /// Indicator if the microphone is enabled.
        /// </summary>
        public bool IsMicrophoneEnabled
        {
            get { return _isMicrophoneEnabled; }
            set { SetProperty(ref _isMicrophoneEnabled, value); }
        }

        private bool _isCameraEnabled = true;

        /// <summary>
        /// Indicator if the camera is enabled.
        /// </summary>
        public bool IsCameraEnabled
        {
            get { return _isCameraEnabled; }
            set { SetProperty(ref _isCameraEnabled, value); }
        }

        private ObservableCollection<MediaDevice> _cameras;

        /// <summary>
        /// The list of available cameras.
        /// </summary>
        public ObservableCollection<MediaDevice> Cameras
        {
            get { return _cameras; }
            set { SetProperty(ref _cameras, value); }
        }

        private MediaDevice _selectedCamera;

        /// <summary>
        /// The selected camera.
        /// </summary>
        public MediaDevice SelectedCamera
        {
            get { return _selectedCamera; }
            set
            {
                SetProperty(ref _selectedCamera, value);

                if (value == null)
                {
                    return;
                }

                Conductor.Instance.Media.SelectVideoDevice(_selectedCamera);
                if (_allCapRes == null)
                {
                    _allCapRes = new ObservableCollection<String>();
                }
                else
                {
                    _allCapRes.Clear();
                }

                var opRes = value.GetVideoCaptureCapabilities();
                opRes.AsTask().ContinueWith(resolutions =>
                {
                    RunOnUiThread(async () =>
                    {
                        if (resolutions.IsFaulted)
                        {
                            Exception ex = resolutions.Exception;
                            while (ex is AggregateException && ex.InnerException != null)
                                ex = ex.InnerException;
                            String errorMsg = "SetSelectedCamera: Failed to GetVideoCaptureCapabilities (Error: " + ex.Message + ")";
                            Debug.WriteLine("[Error] " + errorMsg);
                            var msgDialog = new MessageDialog(errorMsg);
                            await msgDialog.ShowAsync();
                            return;
                        }
                        if (resolutions.Result == null)
                        {
                            String errorMsg = "SetSelectedCamera: Failed to GetVideoCaptureCapabilities (Result is null)";
                            Debug.WriteLine("[Error] " + errorMsg);
                            var msgDialog = new MessageDialog(errorMsg);
                            await msgDialog.ShowAsync();
                            return;
                        }
                        var uniqueRes = resolutions.Result.GroupBy(test => test.ResolutionDescription).Select(grp => grp.First()).ToList();
                        CaptureCapability defaultResolution = null;
                        foreach (var resolution in uniqueRes)
                        {
                            if (defaultResolution == null)
                            {
                                defaultResolution = resolution;
                            }
                            _allCapRes.Add(resolution.ResolutionDescription);
                            if ((resolution.Width == 640) && (resolution.Height == 480))
                            {
                                defaultResolution = resolution;
                            }
                        }
                        var settings = ApplicationData.Current.LocalSettings;
                        string selectedCapResItem = string.Empty;

                        if (settings.Values["SelectedCapResItem"] != null)
                        {
                            selectedCapResItem = (string)settings.Values["SelectedCapResItem"];
                        }

                        if (!string.IsNullOrEmpty(selectedCapResItem) && _allCapRes.Contains(selectedCapResItem))
                        {
                            SelectedCapResItem = selectedCapResItem;
                        }
                        else
                        {
                            SelectedCapResItem = defaultResolution?.ResolutionDescription;
                        }
                    });
                    OnPropertyChanged("AllCapRes");
                });
            }
        }

        private ObservableCollection<MediaDevice> _microphones;

        /// <summary>
        /// The list of available microphones.
        /// </summary>
        public ObservableCollection<MediaDevice> Microphones
        {
            get { return _microphones; }
            set { SetProperty(ref _microphones, value); }
        }

        private MediaDevice _selectedMicrophone;

        /// <summary>
        /// The selected microphone.
        /// </summary>
        public MediaDevice SelectedMicrophone
        {
            get { return _selectedMicrophone; }
            set
            {
                if (SetProperty(ref _selectedMicrophone, value) && value != null)
                {
                    Conductor.Instance.Media.SelectAudioCaptureDevice(_selectedMicrophone);
                }
            }
        }

        private ObservableCollection<MediaDevice> _audioPlayoutDevices;

        /// <summary>
        /// The list of available audio playout devices.
        /// </summary>
        public ObservableCollection<MediaDevice> AudioPlayoutDevices
        {
            get { return _audioPlayoutDevices; }
            set { SetProperty(ref _audioPlayoutDevices, value); }
        }

        private MediaDevice _selectedAudioPlayoutDevice;

        /// <summary>
        /// The selected audio playout device.
        /// </summary>
        public MediaDevice SelectedAudioPlayoutDevice
        {
            get { return _selectedAudioPlayoutDevice; }
            set
            {
                if (SetProperty(ref _selectedAudioPlayoutDevice, value) && value != null)
                {
                    Conductor.Instance.Media.SelectAudioPlayoutDevice(_selectedAudioPlayoutDevice);
                }
            }
        }

        private ObservableCollection<IceServer> _iceServers;

        /// <summary>
        /// The list of Ice servers.
        /// </summary>
        public ObservableCollection<IceServer> IceServers
        {
            get { return _iceServers; }
            set { SetProperty(ref _iceServers, value); }
        }

        private IceServer _selectedIceServer;

        /// <summary>
        /// The selected Ice server.
        /// </summary>
        public IceServer SelectedIceServer
        {
            get { return _selectedIceServer; }
            set
            {
                SetProperty(ref _selectedIceServer, value);
                RemoveSelectedIceServerCommand.RaiseCanExecuteChanged();
            }
        }

        private ObservableCollection<CodecInfo> _audioCodecs;

        /// <summary>
        /// The list of audio codecs.
        /// </summary>
        public ObservableCollection<CodecInfo> AudioCodecs
        {
            get { return _audioCodecs; }
            set { SetProperty(ref _audioCodecs, value); }
        }

        /// <summary>
        /// The selected Audio codec.
        /// </summary>
        public CodecInfo SelectedAudioCodec
        {
            get { return Conductor.Instance.AudioCodec; }
            set
            {
                if (Conductor.Instance.AudioCodec == value)
                {
                    return;
                }
                Conductor.Instance.AudioCodec = value;
                OnPropertyChanged(() => SelectedAudioCodec);
            }
        }

        private ObservableCollection<String> _allCapRes;

        /// <summary>
        /// The list of all capture resolutions.
        /// </summary>
        public ObservableCollection<String> AllCapRes
        {
            get { return _allCapRes ?? (_allCapRes = new ObservableCollection<String>()); }
            set { SetProperty(ref _allCapRes, value); }
        }

        private String _selectedCapResItem;

        /// <summary>
        /// The selected capture resolution.
        /// </summary>
        public String SelectedCapResItem
        {
            get { return _selectedCapResItem; }
            set
            {
                if (AllCapFps == null)
                {
                    AllCapFps = new ObservableCollection<CaptureCapability>();
                }
                else
                {
                    AllCapFps.Clear();
                }
                var opCap = SelectedCamera.GetVideoCaptureCapabilities();
                opCap.AsTask().ContinueWith(caps =>
                {
                    var fpsList = from cap in caps.Result where cap.ResolutionDescription == value select cap;
                    var t = CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            foreach (var fps in fpsList)
                            {
                                AllCapFps.Add(fps);
                            }
                            SelectedCapFpsItem = AllCapFps.First();
                        });
                    OnPropertyChanged("AllCapFps");
                });
                SetProperty(ref _selectedCapResItem, value);
            }
        }

        private ObservableCollection<CaptureCapability> _allCapFps;

        /// <summary>
        /// The list of all capture frame rates.
        /// </summary>
        public ObservableCollection<CaptureCapability> AllCapFps
        {
            get { return _allCapFps ?? (_allCapFps = new ObservableCollection<CaptureCapability>()); }
            set { SetProperty(ref _allCapFps, value); }
        }

        private CaptureCapability _selectedCapFpsItem;

        /// <summary>
        /// The selected capture frame rate.
        /// </summary>
        public CaptureCapability SelectedCapFpsItem
        {
            get { return _selectedCapFpsItem; }
            set
            {
                if (SetProperty(ref _selectedCapFpsItem, value))
                {
                    Conductor.Instance.VideoCaptureProfile = value;
                    Conductor.Instance.UpdatePreferredFrameFormat();

                    var localSettings = ApplicationData.Current.LocalSettings;
                    localSettings.Values["SelectedCapFPSItemFrameRate"] = value?.FrameRate ?? 0;
                }
            }
        }

        private ObservableCollection<CodecInfo> _videoCodecs;

        /// <summary>
        /// The list of video codecs.
        /// </summary>
        public ObservableCollection<CodecInfo> VideoCodecs
        {
            get { return _videoCodecs; }
            set { SetProperty(ref _videoCodecs, value); }
        }

        /// <summary>
        /// The selected video codec.
        /// </summary>
        public CodecInfo SelectedVideoCodec
        {
            get { return Conductor.Instance.VideoCodec; }
            set
            {
                if (Conductor.Instance.VideoCodec == value)
                {
                    return;
                }

                Conductor.Instance.VideoCodec = value;
                OnPropertyChanged(() => SelectedVideoCodec);
            }
        }

        private string _appVersion = "N/A";

        /// <summary>
        /// The application version.
        /// </summary>
        public string AppVersion
        {
            get { return _appVersion; }
            set { SetProperty(ref _appVersion, value); }
        }

        #endregion Bindings

        /// <summary>
        /// Logic to determine if the server is configured.
        /// </summary>
        private void ReevaluateHasServer()
        {
            HasServer = Ip != null && Ip.Valid && Port != null && Port.Valid;
        }

        /// <summary>
        /// Logic to determine if the application is ready to connect to a server.
        /// </summary>
        /// <param name="obj">The sender object.</param>
        /// <returns>True if the application is ready to connect to server.</returns>
        private bool ConnectCommandCanExecute(object obj)
        {
            return !IsConnected && !IsConnecting && Ip.Valid && Port.Valid;
        }

        /// <summary>
        /// Executer command for connecting to server.
        /// </summary>
        /// <param name="obj">The sender object.</param>
        private void ConnectCommandExecute(object obj)
        {
            new Task(() =>
            {
                IsConnecting = true;
                Conductor.Instance.StartLogin(Ip.Value, Port.Value);
            }).Start();
        }

        /// <summary>
        /// Logic to determine if the application is ready to connect to a peer.
        /// </summary>
        /// <param name="obj">The sender object.</param>
        /// <returns>True if the application is ready to connect to a peer.</returns>
        private bool ConnectToPeerCommandCanExecute(object obj)
        {
            return SelectedPeer != null && Peers.Contains(SelectedPeer) && !IsConnectedToPeer && IsReadyToConnect;
        }

        /// <summary>
        /// Executer command to connect to a peer.
        /// </summary>
        /// <param name="obj"></param>
        private void ConnectToPeerCommandExecute(object obj)
        {
            new Task(() => { Conductor.Instance.ConnectToPeer(SelectedPeer); }).Start();
        }

        /// <summary>
        /// Logic to determine if the application is ready to disconnect from peer.
        /// </summary>
        /// <param name="obj">The sender object.</param>
        /// <returns>True if the application is ready to disconnect from a peer.</returns>
        private bool DisconnectFromPeerCommandCanExecute(object obj)
        {
            return IsConnectedToPeer && IsReadyToDisconnect;
        }

        /// <summary>
        /// Executer command to disconnect from a peer.
        /// </summary>
        /// <param name="obj">The sender object.</param>
        private void DisconnectFromPeerCommandExecute(object obj)
        {
            new Task(() => { var task = Conductor.Instance.DisconnectFromPeer(); }).Start();
        }

        /// <summary>
        /// Logic to determine if the application is ready to disconnect from server.
        /// </summary>
        /// <param name="obj">The sender object.</param>
        /// <returns>True if the application is ready to disconnect from the server.</returns>
        private bool DisconnectFromServerCanExecute(object obj)
        {
            if (IsDisconnecting)
            {
                return false;
            }

            return IsConnected;
        }

        /// <summary>
        /// Executer command to disconnect from server.
        /// </summary>
        /// <param name="obj"></param>
        private void DisconnectFromServerExecute(object obj)
        {
            new Task(() =>
            {
                IsDisconnecting = true;
                var task = Conductor.Instance.DisconnectFromServer();
            }).Start();

            Peers?.Clear();
        }

        /// <summary>
        /// Makes the UI scrollable if the controls do not fit the device
        /// screen size.
        /// The UI is not scrollable if connected to a peer.
        /// </summary>
        private void UpdateScrollBarVisibilityTypeHelper()
        {
            if (IsConnectedToPeer)
            {
                ScrollBarVisibilityType = ScrollBarVisibility.Disabled;
            }
            else
            {
                ScrollBarVisibilityType = ScrollBarVisibility.Auto;
            }
        }

        /// <summary>
        /// Loads the settings with predefined and default values.
        /// </summary>
        private void LoadSettings()
        {

            // Default values:

            var peerCcServerIp = new ValidableNonEmptyString("10.10.50.158");
            var peerCcPortInt = 8888;

            var configIceServers = new ObservableCollection<IceServer>();
            // Default values:
            configIceServers.Clear();
            configIceServers.Add(new IceServer("stun.l.google.com:19302", IceServer.ServerType.STUN));
            configIceServers.Add(new IceServer("stun1.l.google.com:19302", IceServer.ServerType.STUN));
            configIceServers.Add(new IceServer("stun2.l.google.com:19302", IceServer.ServerType.STUN));
            configIceServers.Add(new IceServer("stun3.l.google.com:19302", IceServer.ServerType.STUN));
            configIceServers.Add(new IceServer("stun4.l.google.com:19302", IceServer.ServerType.STUN));

            RunOnUiThread(() =>
            {
                IceServers = configIceServers;
                Ip = peerCcServerIp;
                Port = new ValidableIntegerString(peerCcPortInt, 0, 65535);
                ReevaluateHasServer();
            });

            Conductor.Instance.ConfigureIceServers(configIceServers);
        }

        /// <summary>
        /// Saves the Ice servers list.
        /// </summary>
        private void SaveIceServerList()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            string xmlIceServers = XmlSerializer<ObservableCollection<IceServer>>.ToXml(IceServers);
            localSettings.Values["IceServerList"] = xmlIceServers;
        }

        /// <summary>
        /// IP changed event handler.
        /// </summary>
        /// <param name="sender">The sender object.</param>
        /// <param name="e">Property Changed event information.</param>
        private void Ip_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Valid")
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
            ReevaluateHasServer();
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values["PeerCCServerIp"] = _ip.Value;
        }

        /// <summary>
        /// Port changed event handler.
        /// </summary>
        /// <param name="sender">The sender object.</param>
        /// <param name="e">Property Changed event information.</param>
        private void Port_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Valid")
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
            ReevaluateHasServer();
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values["PeerCCServerPort"] = _port.Value;
        }

        /// <summary>
        /// Application suspending event handler.
        /// </summary>
        public async Task OnAppSuspending()
        {
            Conductor.Instance.CancelConnectingToPeer();

            if (IsConnectedToPeer)
            {
                await Conductor.Instance.DisconnectFromPeer();
            }
            if (IsConnected)
            {
                IsDisconnecting = true;
                await Conductor.Instance.DisconnectFromServer();
            }
            Media.OnAppSuspending();
        }

        /// <summary>
        /// Logic to determine if the loopback video UI element needs to be shown.
        /// </summary>
    }
}