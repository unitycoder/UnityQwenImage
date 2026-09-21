using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace UnityLibrary.QwenImage
{
    public class QwenImageEditorWindow : EditorWindow
    {
        private enum SourceMode
        {
            None,
            Texture,
            SceneView,
            GameView
        }

        private enum PromptPreset
        {
            Custom,
            Realistic,
            ConceptArt,
            Stylized,
            MaterialVariation
        }

        [Serializable]
        private class Txt2ImgRequest
        {
            public string prompt;
            public string negative_prompt;
            public int width;
            public int height;
            public int steps;
            public float cfg_scale;
            public long seed;
            public int batch_size = 1;
        }

        [Serializable]
        private class Img2ImgRequest
        {
            public string prompt;
            public string negative_prompt;
            public int width;
            public int height;
            public int steps;
            public float cfg_scale;
            public long seed;
            public int batch_size = 1;
            public float denoising_strength;
            public string[] init_images;
            public string mask;
            public int inpainting_mask_invert;
        }

        [Serializable]
        private class SdApiImageResponse
        {
            public string[] images;
        }

        [SerializeField] private string serverExecutablePath = "Tools/stable-diffusion.cpp/sd-server.exe";
        [SerializeField] private string diffusionModelPath = "Tools/models/qwen-image-2.1-Q4_0.gguf";
        [SerializeField] private string llmPath = "Tools/models/Qwen3VL-8B-Instruct-Q4_K_M.gguf";
        [SerializeField] private string llmVisionPath = "Tools/models/mmproj-Qwen3VL-8B-Instruct-F16.gguf";
        [SerializeField] private string vaePath = "Tools/models/qwen_image_2.1_vae_bf16.safetensors";
        [SerializeField] private string extraServerArguments = "";
        [SerializeField] private int serverPort = 1234;
        [SerializeField] private bool diffusionFlashAttention = true;
        [SerializeField] private bool offloadToCpu = true;
        [SerializeField] private bool autoStartServer = true;
        [SerializeField] private bool showServerOutput = false;
        [SerializeField] private bool stopServerOnUnityExit = true;
        [SerializeField] private bool showServerSetup = false;

        [SerializeField] private string outputFolder = "Assets/Generated";
        [SerializeField] private string prompt = "Make this look realistic. Preserve the composition and camera angle.";
        [SerializeField] private string negativePrompt = "";
        [SerializeField] private int width = 1024;
        [SerializeField] private int height = 1024;
        [SerializeField] private int steps = 30;
        [SerializeField] private float cfgScale = 4.0f;
        [SerializeField] private long seed = -1;
        [SerializeField] private float denoiseStrength = 0.55f;

        [SerializeField] private SourceMode sourceMode = SourceMode.None;
        [SerializeField] private Texture2D sourceImage;
        [SerializeField] private Camera gameCamera;

        [SerializeField] private bool useMask = false;
        [SerializeField] private Texture2D maskImage;
        [SerializeField] private bool invertMask = false;

        [SerializeField] private PromptPreset promptPreset = PromptPreset.Custom;

        [SerializeField] private UnityEngine.Object assignmentTarget;
        [SerializeField] private string materialTextureProperty = "_BaseMap";
        [SerializeField] private bool autoAssignAfterGeneration = false;

        [SerializeField] private List<string> historyPaths = new List<string>();

        private const int MaxHistory = 12;

        private static Process serverProcess;
        private static readonly ConcurrentQueue<string> ServerLogQueue = new ConcurrentQueue<string>();

        private Texture2D lastGeneratedImage;
        private string lastGeneratedPath;
        private Vector2 scroll;
        private bool isBusy;
        private bool serverStarting;
        private bool serverReachable;
        private string status = "Idle";

        [MenuItem("Tools/UnityLibrary/Qwen Image")]
        public static void OpenWindow()
        {
            var window = GetWindow<QwenImageEditorWindow>("Qwen Image");
            window.minSize = new Vector2(500, 750);
        }

        private string ProjectRoot
        {
            get
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            }
        }

        private string ServerBaseUrl
        {
            get
            {
                return "http://127.0.0.1:" + serverPort;
            }
        }

        private void OnEnable()
        {
            EditorApplication.update -= EditorUpdate;
            EditorApplication.update += EditorUpdate;
            EditorApplication.quitting -= OnUnityQuitting;
            EditorApplication.quitting += OnUnityQuitting;
            _ = RefreshServerStatusAsync();
        }

        private void OnDisable()
        {
            EditorApplication.update -= EditorUpdate;
            EditorApplication.quitting -= OnUnityQuitting;
        }

        private void OnUnityQuitting()
        {
            if (stopServerOnUnityExit)
            {
                StopServer();
            }
        }

        private void EditorUpdate()
        {
            bool receivedLog = false;

            while (ServerLogQueue.TryDequeue(out string line))
            {
                receivedLog = true;

                if (showServerOutput)
                {
                    Debug.Log("[Qwen Server] " + line);
                }
            }

            if (receivedLog)
            {
                Repaint();
            }

            if (serverProcess != null && serverProcess.HasExited)
            {
                serverProcess.Dispose();
                serverProcess = null;
            }
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawServerSection();

            EditorGUILayout.Space(10);
            DrawGenerationSection();

            EditorGUILayout.Space(10);
            DrawSourceSection();

            EditorGUILayout.Space(10);
            DrawAssignmentSection();

            EditorGUILayout.Space(10);
            DrawGenerateSection();

            EditorGUILayout.Space(10);
            DrawResultSection();

            EditorGUILayout.Space(10);
            DrawHistorySection();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(status, MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private void DrawServerSection()
        {
            EditorGUILayout.LabelField("Local Qwen Server", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            string processState = IsOwnedServerRunning() ? "Running" : "Not running";
            string apiState = serverReachable ? "Reachable" : "Offline / not checked";

            EditorGUILayout.LabelField("Process", processState);
            EditorGUILayout.LabelField("API", apiState);
            EditorGUILayout.LabelField("URL", ServerBaseUrl);

            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(serverStarting || IsOwnedServerRunning());

            if (GUILayout.Button(serverStarting ? "Starting..." : "Start Server", GUILayout.Height(28)))
            {
                _ = StartServerAsync();
            }

            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!IsOwnedServerRunning());

            if (GUILayout.Button("Stop Server", GUILayout.Height(28)))
            {
                StopServer();
            }

            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Check Server", GUILayout.Height(28)))
            {
                _ = RefreshServerStatusAsync();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            showServerSetup = EditorGUILayout.Foldout(showServerSetup, "Server Setup", true);

            if (showServerSetup)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("Project Root", ProjectRoot);

                serverExecutablePath = EditorGUILayout.TextField("Server", serverExecutablePath);
                diffusionModelPath = EditorGUILayout.TextField("Diffusion Model", diffusionModelPath);
                llmPath = EditorGUILayout.TextField("LLM", llmPath);
                llmVisionPath = EditorGUILayout.TextField("LLM Vision", llmVisionPath);
                vaePath = EditorGUILayout.TextField("VAE", vaePath);
                extraServerArguments = EditorGUILayout.TextField("Extra Arguments", extraServerArguments);

                serverPort = EditorGUILayout.IntField("Port", serverPort);
                diffusionFlashAttention = EditorGUILayout.Toggle("Diffusion Flash Attention", diffusionFlashAttention);
                offloadToCpu = EditorGUILayout.Toggle("Offload To CPU", offloadToCpu);
                autoStartServer = EditorGUILayout.Toggle("Auto Start Server", autoStartServer);
                showServerOutput = EditorGUILayout.Toggle("Log Server Output", showServerOutput);
                stopServerOnUnityExit = EditorGUILayout.Toggle("Stop Server On Unity Exit", stopServerOnUnityExit);

                if (!File.Exists(ResolveProjectPath(llmVisionPath)))
                {
                    EditorGUILayout.HelpBox("LLM Vision file was not found. Text-to-image can still work, but image editing requires the matching mmproj file.", MessageType.Warning);
                }

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Open Tools Folder"))
                {
                    string path = ResolveProjectPath("Tools");

                    if (Directory.Exists(path))
                    {
                        EditorUtility.RevealInFinder(path);
                    }
                }

                if (GUILayout.Button("Open Models Folder"))
                {
                    string path = ResolveProjectPath("Tools/models");

                    if (Directory.Exists(path))
                    {
                        EditorUtility.RevealInFinder(path);
                    }
                }

                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawGenerationSection()
        {
            EditorGUILayout.LabelField("Generation", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            PromptPreset newPreset = (PromptPreset)EditorGUILayout.EnumPopup("Prompt Preset", promptPreset);

            if (newPreset != promptPreset)
            {
                promptPreset = newPreset;

                if (promptPreset != PromptPreset.Custom)
                {
                    ApplyPromptPreset(promptPreset);
                }
            }

            EditorGUILayout.LabelField("Prompt");

            GUIStyle promptStyle = new GUIStyle(EditorStyles.textArea);
            promptStyle.wordWrap = true;

            prompt = EditorGUILayout.TextArea(prompt, promptStyle, GUILayout.MinHeight(80), GUILayout.ExpandWidth(true));

            EditorGUILayout.LabelField("Negative Prompt");
            negativePrompt = EditorGUILayout.TextArea(negativePrompt, promptStyle, GUILayout.MinHeight(40), GUILayout.ExpandWidth(true));

            width = Mathf.Max(64, EditorGUILayout.IntField("Width", width));
            height = Mathf.Max(64, EditorGUILayout.IntField("Height", height));
            steps = EditorGUILayout.IntSlider("Steps", steps, 1, 100);
            cfgScale = EditorGUILayout.Slider("CFG Scale", cfgScale, 0f, 20f);
            seed = EditorGUILayout.LongField("Seed", seed);

            if (sourceMode != SourceMode.None)
            {
                denoiseStrength = EditorGUILayout.Slider("Denoise Strength", denoiseStrength, 0f, 1f);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSourceSection()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            SourceMode newSourceMode = (SourceMode)EditorGUILayout.EnumPopup("Source Mode", sourceMode);

            if (newSourceMode != sourceMode)
            {
                sourceMode = newSourceMode;

                if (sourceMode == SourceMode.None)
                {
                    sourceImage = null;
                }
            }

            if (sourceMode == SourceMode.Texture)
            {
                sourceImage = (Texture2D)EditorGUILayout.ObjectField("Source Image", sourceImage, typeof(Texture2D), false);
            }
            else if (sourceMode == SourceMode.SceneView)
            {
                EditorGUILayout.LabelField("Source", "Current Scene View");

                if (GUILayout.Button("Capture Scene View Now"))
                {
                    CaptureCurrentSource();
                }
            }
            else if (sourceMode == SourceMode.GameView)
            {
                gameCamera = (Camera)EditorGUILayout.ObjectField("Game Camera", gameCamera, typeof(Camera), true);
                EditorGUILayout.HelpBox("In Play Mode the actual Game View is captured. Outside Play Mode the selected camera is rendered. If no camera is assigned, Camera.main is used.", MessageType.Info);

                if (GUILayout.Button("Capture Game View Now"))
                {
                    CaptureCurrentSource();
                }
            }

            if (sourceMode != SourceMode.None)
            {
                EditorGUILayout.Space(4);
                useMask = EditorGUILayout.Toggle("Use Mask", useMask);

                if (useMask)
                {
                    maskImage = (Texture2D)EditorGUILayout.ObjectField("Mask", maskImage, typeof(Texture2D), false);
                    invertMask = EditorGUILayout.Toggle("Invert Mask", invertMask);
                    EditorGUILayout.HelpBox("Use a black and white mask. By default white areas are edited and black areas are protected.", MessageType.Info);
                }
            }

            if (sourceImage != null)
            {
                DrawTexturePreview("Source Preview", sourceImage, 240);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAssignmentSection()
        {
            EditorGUILayout.LabelField("Result Assignment", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            assignmentTarget = EditorGUILayout.ObjectField("Target", assignmentTarget, typeof(UnityEngine.Object), true);
            materialTextureProperty = EditorGUILayout.TextField("Material Property", materialTextureProperty);
            autoAssignAfterGeneration = EditorGUILayout.Toggle("Auto Assign", autoAssignAfterGeneration);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Use Current Selection"))
            {
                assignmentTarget = FindAssignmentTargetFromSelection();
            }

            EditorGUI.BeginDisabledGroup(lastGeneratedImage == null || assignmentTarget == null);

            if (GUILayout.Button("Assign Last Result"))
            {
                ApplyResultToTarget(lastGeneratedImage);
            }

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox("Supported targets: Material, Renderer, RawImage, Image and SpriteRenderer.", MessageType.Info);

            EditorGUILayout.EndVertical();
        }

        private void DrawGenerateSection()
        {
            EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);

            EditorGUI.BeginDisabledGroup(isBusy);

            if (sourceMode == SourceMode.None)
            {
                if (GUILayout.Button("Generate From Text", GUILayout.Height(38)))
                {
                    _ = GenerateTextToImageAsync();
                }
            }
            else
            {
                if (GUILayout.Button("Modify Source Image", GUILayout.Height(38)))
                {
                    _ = GenerateImageToImageAsync();
                }
            }

            EditorGUI.EndDisabledGroup();
        }

        private void DrawResultSection()
        {
            EditorGUILayout.LabelField("Last Result", EditorStyles.boldLabel);

            if (lastGeneratedImage == null && !string.IsNullOrEmpty(lastGeneratedPath))
            {
                lastGeneratedImage = AssetDatabase.LoadAssetAtPath<Texture2D>(lastGeneratedPath);
            }

            if (lastGeneratedImage == null)
            {
                EditorGUILayout.HelpBox("No generated image yet.", MessageType.Info);
                return;
            }

            DrawTexturePreview("", lastGeneratedImage, 350);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Use As Source"))
            {
                sourceMode = SourceMode.Texture;
                sourceImage = lastGeneratedImage;
                status = "Last generated image is now the source image.";
            }

            if (GUILayout.Button("Ping Asset"))
            {
                EditorGUIUtility.PingObject(lastGeneratedImage);
                Selection.activeObject = lastGeneratedImage;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawHistorySection()
        {
            EditorGUILayout.LabelField("History", EditorStyles.boldLabel);

            if (historyPaths == null)
            {
                historyPaths = new List<string>();
            }

            for (int i = historyPaths.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(historyPaths[i]) || AssetDatabase.LoadAssetAtPath<Texture2D>(historyPaths[i]) == null)
                {
                    historyPaths.RemoveAt(i);
                }
            }

            if (historyPaths.Count == 0)
            {
                EditorGUILayout.HelpBox("Generation history is empty.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            for (int i = 0; i < historyPaths.Count; i++)
            {
                string path = historyPaths[i];
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

                if (texture == null)
                {
                    continue;
                }

                EditorGUILayout.BeginHorizontal();

                EditorGUILayout.ObjectField(texture, typeof(Texture2D), false);

                if (GUILayout.Button("Source", GUILayout.Width(60)))
                {
                    sourceMode = SourceMode.Texture;
                    sourceImage = texture;
                }

                if (GUILayout.Button("Ping", GUILayout.Width(50)))
                {
                    EditorGUIUtility.PingObject(texture);
                    Selection.activeObject = texture;
                }

                if (GUILayout.Button("Apply", GUILayout.Width(50)))
                {
                    ApplyResultToTarget(texture);
                }

                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Clear History"))
            {
                historyPaths.Clear();
            }

            EditorGUILayout.EndVertical();
        }

        private async Task GenerateTextToImageAsync()
        {
            if (isBusy)
            {
                return;
            }

            if (!await EnsureServerReadyAsync())
            {
                return;
            }

            isBusy = true;
            status = "Generating image...";
            Repaint();

            try
            {
                Txt2ImgRequest requestBody = new Txt2ImgRequest();
                requestBody.prompt = prompt;
                requestBody.negative_prompt = negativePrompt;
                requestBody.width = MakeMultipleOf16(width);
                requestBody.height = MakeMultipleOf16(height);
                requestBody.steps = steps;
                requestBody.cfg_scale = cfgScale;
                requestBody.seed = seed;
                requestBody.batch_size = 1;

                string json = JsonUtility.ToJson(requestBody);
                string responseText = await PostJsonAsync(ServerBaseUrl + "/sdapi/v1/txt2img", json);
                SdApiImageResponse response = JsonUtility.FromJson<SdApiImageResponse>(responseText);

                ProcessGeneratedResponse(response, "qwen_txt2img");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = "Generation failed: " + ex.Message;
            }
            finally
            {
                isBusy = false;
                Repaint();
            }
        }

        private async Task GenerateImageToImageAsync()
        {
            if (isBusy)
            {
                return;
            }

            if (!await EnsureServerReadyAsync())
            {
                return;
            }

            Texture2D source = GetSourceTexture();

            if (source == null)
            {
                status = "No source image is available.";
                Repaint();
                return;
            }

            isBusy = true;
            status = "Editing image...";
            Repaint();

            try
            {
                Texture2D readableSource = EnsureReadableCopy(source);
                string sourceBase64 = Convert.ToBase64String(readableSource.EncodeToPNG());

                Img2ImgRequest requestBody = new Img2ImgRequest();
                requestBody.prompt = prompt;
                requestBody.negative_prompt = negativePrompt;
                requestBody.width = MakeMultipleOf16(width);
                requestBody.height = MakeMultipleOf16(height);
                requestBody.steps = steps;
                requestBody.cfg_scale = cfgScale;
                requestBody.seed = seed;
                requestBody.batch_size = 1;
                requestBody.denoising_strength = denoiseStrength;
                requestBody.init_images = new[] { sourceBase64 };
                requestBody.inpainting_mask_invert = invertMask ? 1 : 0;

                if (useMask && maskImage != null)
                {
                    Texture2D readableMask = EnsureReadableCopy(maskImage);

                    if (readableMask.width != readableSource.width || readableMask.height != readableSource.height)
                    {
                        Texture2D resizedMask = ResizeTexture(readableMask, readableSource.width, readableSource.height);
                        DestroyImmediate(readableMask);
                        readableMask = resizedMask;
                    }

                    requestBody.mask = Convert.ToBase64String(readableMask.EncodeToPNG());
                    DestroyImmediate(readableMask);
                }

                string json = JsonUtility.ToJson(requestBody);
                string responseText = await PostJsonAsync(ServerBaseUrl + "/sdapi/v1/img2img", json);
                SdApiImageResponse response = JsonUtility.FromJson<SdApiImageResponse>(responseText);

                DestroyImmediate(readableSource);

                ProcessGeneratedResponse(response, "qwen_img2img");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = "Image editing failed: " + ex.Message;
            }
            finally
            {
                isBusy = false;
                Repaint();
            }
        }

        private void ProcessGeneratedResponse(SdApiImageResponse response, string prefix)
        {
            if (response == null || response.images == null || response.images.Length == 0 || string.IsNullOrEmpty(response.images[0]))
            {
                throw new Exception("The server returned no image.");
            }

            byte[] pngBytes = DecodeBase64Image(response.images[0]);
            string assetPath = SavePngToProject(pngBytes, prefix);

            lastGeneratedPath = assetPath;
            lastGeneratedImage = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);

            AddToHistory(assetPath);

            if (autoAssignAfterGeneration && assignmentTarget != null)
            {
                ApplyResultToTarget(lastGeneratedImage);
            }

            status = "Generated: " + assetPath;
        }

        private Texture2D GetSourceTexture()
        {
            if (sourceMode == SourceMode.Texture)
            {
                return sourceImage;
            }

            if (sourceMode == SourceMode.SceneView)
            {
                sourceImage = CaptureSceneViewTexture(MakeMultipleOf16(width), MakeMultipleOf16(height));
                return sourceImage;
            }

            if (sourceMode == SourceMode.GameView)
            {
                sourceImage = CaptureGameViewTexture(MakeMultipleOf16(width), MakeMultipleOf16(height));
                return sourceImage;
            }

            return null;
        }

        private void CaptureCurrentSource()
        {
            try
            {
                Texture2D captured = GetSourceTexture();

                if (captured == null)
                {
                    status = "Nothing was captured.";
                    return;
                }

                sourceImage = captured;
                status = "Source captured.";
                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = "Capture failed: " + ex.Message;
            }
        }

        private Texture2D CaptureSceneViewTexture(int captureWidth, int captureHeight)
        {
            SceneView sceneView = SceneView.lastActiveSceneView;

            if (sceneView == null || sceneView.camera == null)
            {
                throw new Exception("No active Scene View was found.");
            }

            return CaptureCameraTexture(sceneView.camera, captureWidth, captureHeight);
        }

        private Texture2D CaptureGameViewTexture(int captureWidth, int captureHeight)
        {
            if (Application.isPlaying)
            {
                Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();

                if (screenshot == null)
                {
                    throw new Exception("Game View capture failed.");
                }

                Texture2D resized = ResizeTexture(screenshot, captureWidth, captureHeight);
                DestroyImmediate(screenshot);
                return resized;
            }

            Camera cameraToUse = gameCamera;

            if (cameraToUse == null)
            {
                cameraToUse = Camera.main;
            }

            if (cameraToUse == null)
            {
                Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();

                foreach (Camera camera in cameras)
                {
                    if (camera == null || !camera.gameObject.scene.IsValid())
                    {
                        continue;
                    }

                    cameraToUse = camera;
                    break;
                }
            }

            if (cameraToUse == null)
            {
                throw new Exception("No Game Camera was found. Assign one in the Game Camera field.");
            }

            return CaptureCameraTexture(cameraToUse, captureWidth, captureHeight);
        }

        private Texture2D CaptureCameraTexture(Camera camera, int captureWidth, int captureHeight)
        {
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(captureWidth, captureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D texture = new Texture2D(captureWidth, captureHeight, TextureFormat.RGBA32, false);

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
                texture.Apply();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            return texture;
        }

        private Texture2D EnsureReadableCopy(Texture2D source)
        {
            if (source == null)
            {
                return null;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

            Graphics.Blit(source, renderTexture);
            RenderTexture.active = renderTexture;

            Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            copy.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTexture);

            return copy;
        }

        private Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

            Graphics.Blit(source, renderTexture);
            RenderTexture.active = renderTexture;

            Texture2D resized = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            resized.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            resized.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTexture);

            return resized;
        }

        private void ApplyPromptPreset(PromptPreset preset)
        {
            if (preset == PromptPreset.Realistic)
            {
                prompt = "Make this look photorealistic. Preserve the original composition, geometry, proportions and camera angle. Replace game-like materials with realistic materials and improve lighting, shadows, reflections and surface detail.";
            }
            else if (preset == PromptPreset.ConceptArt)
            {
                prompt = "Turn this into high quality professional concept art. Preserve the original composition, geometry and camera angle while improving lighting, atmosphere, materials and environmental detail.";
            }
            else if (preset == PromptPreset.Stylized)
            {
                prompt = "Create a polished stylized version of this image. Preserve the composition and major shapes while improving colors, lighting, materials and visual clarity.";
            }
            else if (preset == PromptPreset.MaterialVariation)
            {
                prompt = "Preserve the geometry, composition and camera angle. Create a new believable material variation with detailed surfaces, realistic roughness, reflections and lighting.";
            }
        }

        private UnityEngine.Object FindAssignmentTargetFromSelection()
        {
            if (Selection.activeObject is Material)
            {
                return Selection.activeObject;
            }

            GameObject selectedGameObject = Selection.activeGameObject;

            if (selectedGameObject == null && Selection.activeObject is Component selectedComponent)
            {
                selectedGameObject = selectedComponent.gameObject;
            }

            if (selectedGameObject == null)
            {
                status = "Current selection is not a supported assignment target.";
                return null;
            }

            RawImage rawImage = selectedGameObject.GetComponent<RawImage>();

            if (rawImage != null)
            {
                return rawImage;
            }

            Image image = selectedGameObject.GetComponent<Image>();

            if (image != null)
            {
                return image;
            }

            SpriteRenderer spriteRenderer = selectedGameObject.GetComponent<SpriteRenderer>();

            if (spriteRenderer != null)
            {
                return spriteRenderer;
            }

            Renderer renderer = selectedGameObject.GetComponent<Renderer>();

            if (renderer != null)
            {
                return renderer;
            }

            status = "Current selection is not a supported assignment target.";
            return null;
        }

        private void ApplyResultToTarget(Texture2D texture)
        {
            if (texture == null)
            {
                status = "There is no generated image to assign.";
                return;
            }

            if (assignmentTarget == null)
            {
                status = "No assignment target is selected.";
                return;
            }

            if (assignmentTarget is Material material)
            {
                AssignTextureToMaterial(material, texture);
                status = "Image assigned to material.";
                return;
            }

            if (assignmentTarget is Renderer renderer)
            {
                if (renderer.sharedMaterial == null)
                {
                    status = "Renderer has no shared material.";
                    return;
                }

                AssignTextureToMaterial(renderer.sharedMaterial, texture);
                EditorUtility.SetDirty(renderer);
                status = "Image assigned to renderer material.";
                return;
            }

            if (assignmentTarget is RawImage rawImage)
            {
                Undo.RecordObject(rawImage, "Assign Qwen Image");
                rawImage.texture = texture;
                EditorUtility.SetDirty(rawImage);
                status = "Image assigned to RawImage.";
                return;
            }

            if (assignmentTarget is Image image)
            {
                Sprite sprite = LoadTextureAsSprite(texture);

                if (sprite == null)
                {
                    status = "Failed to import generated image as Sprite.";
                    return;
                }

                Undo.RecordObject(image, "Assign Qwen Image");
                image.sprite = sprite;
                EditorUtility.SetDirty(image);
                status = "Image assigned to UI Image.";
                return;
            }

            if (assignmentTarget is SpriteRenderer spriteRenderer)
            {
                Sprite sprite = LoadTextureAsSprite(texture);

                if (sprite == null)
                {
                    status = "Failed to import generated image as Sprite.";
                    return;
                }

                Undo.RecordObject(spriteRenderer, "Assign Qwen Image");
                spriteRenderer.sprite = sprite;
                EditorUtility.SetDirty(spriteRenderer);
                status = "Image assigned to SpriteRenderer.";
                return;
            }

            status = "Unsupported assignment target.";
        }

        private void AssignTextureToMaterial(Material material, Texture2D texture)
        {
            Undo.RecordObject(material, "Assign Qwen Image");

            if (!string.IsNullOrEmpty(materialTextureProperty) && material.HasProperty(materialTextureProperty))
            {
                material.SetTexture(materialTextureProperty, texture);
            }
            else
            {
                material.mainTexture = texture;
            }

            EditorUtility.SetDirty(material);
        }

        private Sprite LoadTextureAsSprite(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);

            if (string.IsNullOrEmpty(path))
            {
                return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            }

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;

            if (importer != null && importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private void DrawTexturePreview(string label, Texture2D texture, float maxHeight)
        {
            if (!string.IsNullOrEmpty(label))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            }

            if (texture == null)
            {
                return;
            }

            float availableWidth = Mathf.Max(position.width - 60f, 100f);
            float aspect = (float)texture.width / Mathf.Max(1, texture.height);
            float previewWidth = Mathf.Min(availableWidth, texture.width);
            float previewHeight = previewWidth / Mathf.Max(0.0001f, aspect);

            if (previewHeight > maxHeight)
            {
                previewHeight = maxHeight;
                previewWidth = previewHeight * aspect;
            }

            Rect rect = GUILayoutUtility.GetRect(previewWidth, previewHeight, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(rect, texture, null, ScaleMode.ScaleToFit);
        }

        private async Task<bool> EnsureServerReadyAsync()
        {
            if (await IsServerReachableAsync())
            {
                serverReachable = true;
                return true;
            }

            serverReachable = false;

            if (!autoStartServer)
            {
                status = "Qwen server is not running.";
                Repaint();
                return false;
            }

            return await StartServerAsync();
        }

        private async Task<bool> StartServerAsync()
        {
            if (serverStarting)
            {
                return false;
            }

            if (await IsServerReachableAsync())
            {
                serverReachable = true;
                status = "Qwen server is already running.";
                Repaint();
                return true;
            }

            string executable = ResolveProjectPath(serverExecutablePath);
            string diffusionModel = ResolveProjectPath(diffusionModelPath);
            string llm = ResolveProjectPath(llmPath);
            string llmVision = ResolveProjectPath(llmVisionPath);
            string vae = ResolveProjectPath(vaePath);

            if (!File.Exists(executable))
            {
                status = "Server executable not found: " + executable;
                Debug.LogError(status);
                return false;
            }

            if (!File.Exists(diffusionModel))
            {
                status = "Diffusion model not found: " + diffusionModel;
                Debug.LogError(status);
                return false;
            }

            if (!File.Exists(llm))
            {
                status = "LLM not found: " + llm;
                Debug.LogError(status);
                return false;
            }

            if (!File.Exists(vae))
            {
                status = "VAE not found: " + vae;
                Debug.LogError(status);
                return false;
            }

            serverStarting = true;
            status = "Starting Qwen server...";
            Repaint();

            try
            {
                StringBuilder arguments = new StringBuilder();

                AppendArgument(arguments, "--diffusion-model", diffusionModel);
                AppendArgument(arguments, "--llm", llm);

                if (File.Exists(llmVision))
                {
                    AppendArgument(arguments, "--llm_vision", llmVision);
                }

                AppendArgument(arguments, "--vae", vae);
                AppendArgument(arguments, "--listen-ip", "127.0.0.1");
                AppendArgument(arguments, "--listen-port", serverPort.ToString());

                if (diffusionFlashAttention)
                {
                    arguments.Append(" --diffusion-fa");
                }

                if (offloadToCpu)
                {
                    arguments.Append(" --offload-to-cpu");
                }

                if (!string.IsNullOrWhiteSpace(extraServerArguments))
                {
                    arguments.Append(" ");
                    arguments.Append(extraServerArguments.Trim());
                }

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = executable;
                startInfo.Arguments = arguments.ToString().Trim();
                startInfo.WorkingDirectory = ProjectRoot;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                serverProcess = new Process();
                serverProcess.StartInfo = startInfo;
                serverProcess.EnableRaisingEvents = true;
                serverProcess.OutputDataReceived += ServerOutputReceived;
                serverProcess.ErrorDataReceived += ServerOutputReceived;
                serverProcess.Exited += ServerExited;

                Debug.Log("Starting Qwen server:\n" + executable + " " + startInfo.Arguments);

                if (!serverProcess.Start())
                {
                    throw new Exception("Process.Start returned false.");
                }

                serverProcess.BeginOutputReadLine();
                serverProcess.BeginErrorReadLine();

                for (int i = 0; i < 180; i++)
                {
                    await Task.Delay(1000);

                    if (serverProcess == null || serverProcess.HasExited)
                    {
                        status = "Qwen server exited while starting.";
                        serverReachable = false;
                        return false;
                    }

                    if (await IsServerReachableAsync())
                    {
                        serverReachable = true;
                        status = "Qwen server is ready.";
                        Repaint();
                        return true;
                    }
                }

                status = "Server process is running, but the API did not become reachable.";
                serverReachable = false;
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = "Failed to start Qwen server: " + ex.Message;
                serverReachable = false;
                return false;
            }
            finally
            {
                serverStarting = false;
                Repaint();
            }
        }

        private void StopServer()
        {
            try
            {
                if (serverProcess != null)
                {
                    if (!serverProcess.HasExited)
                    {
                        serverProcess.Kill();
                        serverProcess.WaitForExit(3000);
                    }

                    serverProcess.Dispose();
                    serverProcess = null;
                }

                serverReachable = false;
                serverStarting = false;
                status = "Qwen server stopped.";
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = "Failed to stop server: " + ex.Message;
            }

            Repaint();
        }

        private async Task RefreshServerStatusAsync()
        {
            serverReachable = await IsServerReachableAsync();

            if (serverReachable)
            {
                status = "Qwen server is reachable.";
            }
            else if (!serverStarting)
            {
                status = "Qwen server is offline.";
            }

            Repaint();
        }

        private async Task<bool> IsServerReachableAsync()
        {
            try
            {
                using (UnityWebRequest request = UnityWebRequest.Get(ServerBaseUrl + "/v1/models"))
                {
                    request.timeout = 2;
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                    while (!operation.isDone)
                    {
                        await Task.Yield();
                    }

                    return request.result == UnityWebRequest.Result.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool IsOwnedServerRunning()
        {
            return serverProcess != null && !serverProcess.HasExited;
        }

        private static void ServerOutputReceived(object sender, DataReceivedEventArgs args)
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                ServerLogQueue.Enqueue(args.Data);
            }
        }

        private static void ServerExited(object sender, EventArgs args)
        {
            ServerLogQueue.Enqueue("Server process exited.");
        }

        private void AppendArgument(StringBuilder arguments, string name, string value)
        {
            if (arguments.Length > 0)
            {
                arguments.Append(" ");
            }

            arguments.Append(name);
            arguments.Append(" ");
            arguments.Append(QuoteArgument(value));
        }

        private string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private string ResolveProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            return Path.GetFullPath(Path.Combine(ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        }

        private async Task<string> PostJsonAsync(string url, string json)
        {
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string responseBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    throw new Exception("HTTP error: " + request.error + "\n" + responseBody);
                }

                return request.downloadHandler.text;
            }
        }

        private byte[] DecodeBase64Image(string base64)
        {
            if (string.IsNullOrEmpty(base64))
            {
                throw new Exception("Image response was empty.");
            }

            string cleaned = base64.Trim();

            if (cleaned.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int commaIndex = cleaned.IndexOf(',');

                if (commaIndex >= 0 && commaIndex < cleaned.Length - 1)
                {
                    cleaned = cleaned.Substring(commaIndex + 1);
                }
            }

            return Convert.FromBase64String(cleaned);
        }

        private string SavePngToProject(byte[] pngBytes, string prefix)
        {
            string relativeFolder = outputFolder.Replace("\\", "/").Trim();

            if (string.IsNullOrEmpty(relativeFolder))
            {
                relativeFolder = "Assets/Generated";
            }

            if (!relativeFolder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !relativeFolder.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Generated images must be saved under Assets/. Example: Assets/Generated");
            }

            string absoluteFolder = Path.Combine(ProjectRoot, relativeFolder.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(absoluteFolder);

            string fileName = prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
            string absolutePath = Path.Combine(absoluteFolder, fileName);
            string relativePath = relativeFolder.TrimEnd('/') + "/" + fileName;

            File.WriteAllBytes(absolutePath, pngBytes);

            AssetDatabase.ImportAsset(relativePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            return relativePath;
        }

        private void AddToHistory(string assetPath)
        {
            if (historyPaths == null)
            {
                historyPaths = new List<string>();
            }

            historyPaths.Remove(assetPath);
            historyPaths.Insert(0, assetPath);

            while (historyPaths.Count > MaxHistory)
            {
                historyPaths.RemoveAt(historyPaths.Count - 1);
            }
        }

        private int MakeMultipleOf16(int value)
        {
            value = Mathf.Max(64, value);
            return Mathf.RoundToInt(value / 16f) * 16;
        }
    }
}