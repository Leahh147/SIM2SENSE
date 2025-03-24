using UnityEngine;
using UnityEngine.InputSystem.XR;
using WhacAMole.Scripts.Audio;

namespace UserInTheBox
{
    public class SimulatedUser : MonoBehaviour
    {
        public Transform leftHandController, rightHandController;
        public Camera mainCamera;
        public AudioListener audioListener;
        public bool audioModeOn = false;
        public AudioManager audioManager;
        public RLEnv env;
        private ZmqServer _server;
        private string _port;
        private Rect _rect;
        private RenderTexture _renderTexture;
        private RenderTexture _lightMap;
        private Texture2D _tex;
        private bool _sendReply;
        private byte[] _previousImage;
        [SerializeField] private bool simulated;
        private float[] _audioData;
        public void Awake()
        {
            _port = UitBUtils.GetOptionalKeywordArgument("port", "5555");
            enabled = UitBUtils.GetOptionalArgument("simulated") | simulated;

            if (enabled)
            {
                mainCamera.enabled = false;
                mainCamera.depthTextureMode = DepthTextureMode.Depth;
                mainCamera.gameObject.AddComponent<RenderShader>();
                mainCamera.fieldOfView = 90;
                mainCamera.nearClipPlane = 0.01f;
                mainCamera.farClipPlane = 10;

                if (mainCamera.GetComponent<TrackedPoseDriver>() != null)
                {
                    mainCamera.GetComponent<TrackedPoseDriver>().enabled = false;
                }

                // Change the parent of the audio listener to be the main camera, and set its position to (0,0,0) relative to the camera
                audioListener.transform.SetParent(mainCamera.transform);
                audioListener.transform.localPosition = Vector3.zero;

            }
            else
            {
                gameObject.SetActive(false);
            }

            audioManager.m_AudioSensorComponent.CreateSensors();
            // for pipeline purposes, I just set the args manually because the utils are not taking args properly
            // audioModeOn = true;
            // if (audioModeOn) {
            //     audioManager.SignalType = "Mono";
            //     audioManager.SampleType = "Amplitude";
            //     Debug.Log("Audio mode on, using signal type " + audioManager.SignalType + " and sample type " + audioManager.SampleType);
            // }

            // comment the following lines if not using standalone application
            string audioKeyword = UitBUtils.GetKeywordArgument("audioModeOn");
            audioModeOn = audioKeyword == "true" ? true : false;
            if (audioModeOn) {
                string signalType_ = UitBUtils.GetOptionalKeywordArgument("signalType", "Mono");
                string sampleType_ = UitBUtils.GetOptionalKeywordArgument("sampleType", "Amplitude");
                audioManager.SignalType = signalType_;
                audioManager.SampleType = sampleType_;
                Debug.Log("Audio mode on, using signal type " + audioManager.SignalType + " and sample type " + audioManager.SampleType);
            }

        }

        public void Start()
        {
            int timeOutSeconds = _port == "5555" ? 600 : 60;
            _server = new ZmqServer(_port, timeOutSeconds);
            var timeOptions = _server.WaitForHandshake();
            Time.timeScale = timeOptions.timeScale;
            Application.targetFrameRate = timeOptions.sampleFrequency * (int)Time.timeScale;
            Time.fixedDeltaTime = timeOptions.fixedDeltaTime > 0 ? timeOptions.fixedDeltaTime : timeOptions.timestep;
            Time.maximumDeltaTime = 1.0f / Application.targetFrameRate;

            Screen.SetResolution(1, 1, false);
            const int width = 120;
            const int height = 80;
            _rect = new Rect(0, 0, width, height);
            _renderTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGBHalf);
            _tex = new Texture2D(width, height, TextureFormat.RGBAHalf, false);
            _lightMap = new RenderTexture(width, height, 16);
            _lightMap.name = "stupid_hack";
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
            Debug.Log("Audio data from unity side" + _audioData + "at time " + Time.time);

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