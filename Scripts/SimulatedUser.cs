using UnityEngine;
using UnityEngine.InputSystem.XR;
using System.Collections.Generic;

namespace UserInTheBox
{
    /// <summary>
    /// A simplified version of SimulatedUser for the temporal task
    /// Only includes the essential components needed for the task
    /// </summary>
    public class SimulatedUser : MonoBehaviour
    {
        public Transform leftHandController, rightHandController;
        public Transform fingerTipTransform;
        public Camera mainCamera;
        public AudioListener audioListener;
        public bool audioModeOn = false;
        public RLEnv env;
        
        // Server communication
        private ZmqServer _server;
        private string _port;
        private Rect _rect;
        private RenderTexture _renderTexture;
        private RenderTexture _lightMap;
        private Texture2D _tex;
        private bool _sendReply;
        private byte[] _previousImage;
        [SerializeField] private bool simulated;
        
        // Audio data
        private float[] _audioData;
        private AudioSource _audioSource;
        public AudioManager audioManager;
        
        public void Awake()
        {
            _port = UitBUtils.GetOptionalKeywordArgument("port", "5555");
            enabled = UitBUtils.GetOptionalArgument("simulated") | simulated;

            if (enabled)
            {
                // Configure camera for simulation
                mainCamera.enabled = false;
                mainCamera.depthTextureMode = DepthTextureMode.Depth;
                mainCamera.fieldOfView = 90;
                mainCamera.nearClipPlane = 0.01f;
                mainCamera.farClipPlane = 10;

                if (mainCamera.GetComponent<TrackedPoseDriver>() != null)
                {
                    mainCamera.GetComponent<TrackedPoseDriver>().enabled = false;
                }

                // Set up audio listener
                audioListener.transform.SetParent(mainCamera.transform);
                audioListener.transform.localPosition = Vector3.zero;
                
                // Set up audio source for sensing
                _audioSource = GetComponent<AudioSource>();
                if (_audioSource == null)
                {
                    _audioSource = gameObject.AddComponent<AudioSource>();
                }
            }
            else
            {
                gameObject.SetActive(false);
            }
            
            // Make sure fingertip transform is set
            if (fingerTipTransform == null && rightHandController != null)
            {
                fingerTipTransform = rightHandController;
                Debug.Log("Using right controller as fingertip transform");
            }

            audioManager.m_AudioSensorComponent.CreateSensors();

            // Comment the following lines if debugging
            string audioKeyword = UitBUtils.GetKeywordArgument("audioModeOn");
            audioModeOn = audioKeyword == "true";
            if (audioModeOn) {
                string signalType_ = UitBUtils.GetOptionalKeywordArgument("signalType", "Mono");
                string sampleType_ = UitBUtils.GetOptionalKeywordArgument("sampleType", "Amplitude");
                audioManager.SignalType = signalType_;
                audioManager.SampleType = sampleType_;
            }
        }

        public void Start()
        {
            // Set up ZMQ communication
            int timeOutSeconds = _port == "5555" ? 600 : 60;
            _server = new ZmqServer(_port, timeOutSeconds);
            var timeOptions = _server.WaitForHandshake();
            
            // Configure time settings
            Time.timeScale = timeOptions.timeScale;
            Application.targetFrameRate = timeOptions.sampleFrequency * (int)Time.timeScale;
            Time.fixedDeltaTime = timeOptions.fixedDeltaTime > 0 ? timeOptions.fixedDeltaTime : timeOptions.timestep;
            Time.maximumDeltaTime = 1.0f / Application.targetFrameRate;

            // Set up rendering for observations
            Screen.SetResolution(1, 1, false);
            const int width = 120;
            const int height = 80;
            _rect = new Rect(0, 0, width, height);
            _renderTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGBHalf);
            _tex = new Texture2D(width, height, TextureFormat.RGBAHalf, false);
            _lightMap = new RenderTexture(width, height, 16);
            _lightMap.name = "light_map";
            _lightMap.enableRandomWrite = true;
            _lightMap.Create();
        }

        public void Update()
        {
            _sendReply = false;
            SimulatedUserState previousState = _server.GetSimulationState();

            if (previousState != null && Time.fixedTime < previousState.nextTimestep)
            {
                return;
            }
            _sendReply = true;
            var state = _server.ReceiveState();
            UpdateAnchors(state);

            if (state.quitApplication)
            {
                #if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
                #else
                    Application.Quit();
                #endif
            }
            else if (state.reset)
            {
                env.Reset();
                audioManager.m_AudioSensorComponent.OnSensorReset();
            }
        }

        private void UpdateAnchors(SimulatedUserState state)
        {
            mainCamera.transform.SetPositionAndRotation(state.headsetPosition, state.headsetRotation);
            leftHandController.SetPositionAndRotation(state.leftControllerPosition, state.leftControllerRotation);
            rightHandController.SetPositionAndRotation(state.rightControllerPosition, state.rightControllerRotation);

            if (env.overrideHeadsetOrientation)
            {
                mainCamera.transform.rotation = env.simulatedUserHeadsetOrientation;
            }
        }
        public void LateUpdate() // 20Hz
        {
            if (!_sendReply)
            {
                return;
            }

            mainCamera.targetTexture = _renderTexture;
            mainCamera.Render();
            RenderTexture.active = _lightMap;
            Graphics.Blit(_renderTexture, _lightMap);
            _tex.ReadPixels(_rect, 0, 0);
            RenderTexture.active = null;
            _previousImage = _tex.EncodeToPNG();
            AudioSampling();

            var samples2D = audioManager.m_AudioSensorComponent.Sensor.Buffer.Samples;
            
            _audioData = samples2D.Flatten();

            var reward = env.GetReward();
            var isFinished = env.IsFinished() || _server.GetSimulationState().isFinished;
            var timeFeature = env.GetTimeFeature();
            var logDict = env.GetLogDict();

            // Send observation to client with audio data
            _server.SendObservation(isFinished, reward, _previousImage, _audioData, timeFeature, logDict);
        }

        public void AudioSampling()
        {
            audioManager.m_AudioSensorComponent.SampleAudioinSimulatedUser();
        }
        private void OnDestroy()
        {   
            audioManager.m_AudioSensorComponent.OnDestroy();
            _server?.Close();
        }

        private void OnApplicationQuit()
        {
            _server?.Close();
        }

        public string getPort()
        {
            return _port;
        }
    }
}