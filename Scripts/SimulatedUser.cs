using UnityEngine;
using UnityEngine.InputSystem.XR;

namespace UserInTheBox
{
    public class SimulatedUser : MonoBehaviour
    {
        public Transform leftHandController, rightHandController;
        public Camera mainCamera;
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

        private AudioListener _audioListener;
        private float[] _audioData;
        private int _sampleRate = 44100; // Example sample rate

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

                // Add AudioListener component
                _audioListener = gameObject.AddComponent<AudioListener>();
            }
            else
            {
                gameObject.SetActive(false);
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

        public void LateUpdate()
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

            // Capture audio data
            _audioData = new float[_sampleRate];
            AudioListener.GetOutputData(_audioData, 0);

            // Convert audio data to byte array
            byte[] audioBytes = new byte[_audioData.Length * sizeof(float)];
            Buffer.BlockCopy(_audioData, 0, audioBytes, 0, audioBytes.Length);

            var reward = env.GetReward();
            var isFinished = env.IsFinished() || _server.GetSimulationState().isFinished;
            var timeFeature = env.GetTimeFeature();
            var logDict = env.GetLogDict();

            // Send observation to client with audio data
            _server.SendObservation(isFinished, reward, _previousImage, audioBytes, timeFeature, logDict);
        }

        private void OnDestroy()
        {
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