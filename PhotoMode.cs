using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace FreeCameraAndCollision
{
    // Photo mode, opened from the pause menu: the game stays paused with the menu hidden, a panel (built further down) sets the
    // time of day, the weather and the picture, and everything it changed is put back on leaving. Sits on the BoatCamera object
    // beside the free camera and is enabled only while open.
    [DefaultExecutionOrder(10001)]
    internal sealed class PhotoMode : MonoBehaviour
    {
        private const string PlayerModelGuid = "com.diamondminer99.playermodel";
        private const string PlayerModelEntryId = "freecam_photomode";
        private const string GameMenuButtonName = "freecam photo mode button";

        private static readonly FieldInfo SettingsUiField = AccessTools.Field(typeof(StartMenu), "settingsUI");
        private static readonly FieldInfo LogoField = AccessTools.Field(typeof(StartMenu), "logo");
        private static readonly MethodInfo EnableSettingsMenuMethod = AccessTools.Method(typeof(StartMenu), "EnableSettingsMenu");
        private static readonly MethodInfo DisableSettingsMenuMethod = AccessTools.Method(typeof(StartMenu), "DisableSettingsMenu");
        private static readonly FieldInfo WeatherLerpField = AccessTools.Field(typeof(Weather), "lerp");
        private static readonly FieldInfo[] WeatherParticleFields =
        {
            AccessTools.Field(typeof(Weather), "upperClouds"),
            AccessTools.Field(typeof(Weather), "lowerClouds"),
            AccessTools.Field(typeof(Weather), "rain"),
            AccessTools.Field(typeof(Weather), "outerRain"),
            AccessTools.Field(typeof(Weather), "rainSplash")
        };

        // Player Model's pause menu and body, by reflection; null without Player Model.
        private static MethodInfo playerModelMenuHide;
        private static MethodInfo playerModelMenuReopen;
        private static MethodInfo playerModelForceVisible;
        private static PropertyInfo playerModelBody;

        private static PhotoMode instance;
        private static int closedFrame = -1;

        private FreeCameraController controller;
        private FreeCameraAndCollisionPlugin plugin;
        private StartMenu menu;
        private GameObject settingsUi;
        private GameObject logo;
        private MouseButtonPointer menuPointer;
        private bool settingsUiWasShown;
        private bool logoWasShown;
        private bool menuPointerWasEnabled;

        private Sun sun;
        private Light sunLight;
        private SunlightNightIntensity sunlightIntensity;
        private float savedGlobalTime;
        private float savedSunIntensity;
        private float localTimeOffset;

        private Weather weather;
        private WeatherSet savedWeatherTarget;
        private float savedWeatherLerp;
        private readonly float[] savedFog = new float[8];
        private float savedDayFogMult;
        private float savedNightFogMult;
        private ParticleSystem[] weatherParticles;
        private bool[] savedParticleUnscaled;
        private bool weatherFogDirty;

        private PostProcessingBehaviour postProcessing;
        private PostProcessingProfile originalProfile;
        private PostProcessingProfile photoProfile;
        private float savedFieldOfView;

        private Camera uiCamera;
        private int savedUiCullingMask;
        private Renderer[] hudRenderers;
        private bool[] savedHudRendererStates;
        private bool hudHidden;
        private bool uiHidden;

        private GameObject playerBody;
        private bool bodyHidden;
        private bool bodyForcedVisible;

        internal static bool IsOpen { get; private set; }

        // The game's ESC handling is skipped while photo mode is open and on the frame it closed, so the ESC that closed it
        // does not also resume the game.
        internal static bool ClaimsPauseKey
        {
            get
            {
                return IsOpen || closedFrame == Time.frameCount;
            }
        }

        internal static bool IsPanelShown
        {
            get
            {
                return IsOpen && instance != null && !instance.uiHidden;
            }
        }

        internal static bool IsMouseOverPanel
        {
            get
            {
                return IsPanelShown && instance.mouseOverPanel;
            }
        }

        internal static bool UsesPlayerModelMenu
        {
            get
            {
                return playerModelMenuHide != null;
            }
        }

        internal void Initialize(FreeCameraController owner, FreeCameraAndCollisionPlugin ownerPlugin)
        {
            controller = owner;
            plugin = ownerPlugin;
            instance = this;
            if (!IsOpen)
            {
                enabled = false;
            }
        }

        // Adds the Photo Mode entry to Player Model's pause menu and chains its ESC claim. Runs once all plugins have loaded.
        internal static void ConnectPlayerModel(FreeCameraAndCollisionPlugin ownerPlugin)
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(PlayerModelGuid))
            {
                return;
            }

            try
            {
                Type menuType = AccessTools.TypeByName("SailwindPlayerModel.ModPauseMenu");
                Type entryType = menuType?.GetNestedType("Entry");
                FieldInfo claimField = AccessTools.Field(AccessTools.TypeByName("SailwindPlayerModel.PauseMenuPatches"), "ExtraPauseKeyClaim");
                MethodInfo register = menuType != null ? AccessTools.Method(menuType, "Register") : null;
                MethodInfo hide = menuType != null ? AccessTools.Method(menuType, "Hide") : null;
                MethodInfo reopen = menuType != null ? AccessTools.Method(menuType, "Reopen") : null;
                if (entryType == null || claimField == null || register == null || hide == null || reopen == null)
                {
                    ownerPlugin.LogInitializationError("Photo mode: Player Model's pause menu was not found; the Photo Mode button is not added.");
                    return;
                }

                object entry = Activator.CreateInstance(entryType);
                entryType.GetField("Id").SetValue(entry, PlayerModelEntryId);
                entryType.GetField("Order").SetValue(entry, 200);
                entryType.GetField("Label").SetValue(entry, (Func<string>)(() => "Photo Mode"));
                entryType.GetField("OnClick").SetValue(entry, (Action)OpenFromMenu);
                Func<bool> previousClaim = claimField.GetValue(null) as Func<bool>;
                claimField.SetValue(null, (Func<bool>)(() => ClaimsPauseKey || (previousClaim != null && previousClaim())));
                register.Invoke(null, new object[] { entry });
                playerModelMenuHide = hide;
                playerModelMenuReopen = reopen;

                Type modelType = AccessTools.TypeByName("SailwindPlayerModel.PlayerModel");
                playerModelForceVisible = modelType != null ? AccessTools.Method(modelType, "ForceVisible") : null;
                playerModelBody = modelType != null ? AccessTools.Property(modelType, "Body") : null;
            }
            catch (Exception exception)
            {
                playerModelMenuHide = null;
                ownerPlugin.LogInitializationError("Photo mode: could not connect to Player Model's pause menu: " + exception.GetBaseException().Message);
            }
        }

        // Without Player Model: a copy of the game's Recover Boat button in its pause menu, with its own text.
        internal static void EnsureGameMenuButton(StartMenu startMenu)
        {
            GameObject ui = SettingsUiField?.GetValue(startMenu) as GameObject;
            Transform template = ui != null ? ui.transform.Find("button recover") : null;
            if (template == null || template.parent.Find(GameMenuButtonName) != null)
            {
                return;
            }

            Transform button = Instantiate(template.gameObject, template.parent).transform;
            button.name = GameMenuButtonName;
            button.localPosition = GameMenuButtonPosition;
            button.localRotation = template.localRotation;
            button.localScale = template.localScale;
            button.gameObject.SetActive(true);
            TextMesh[] texts = button.GetComponentsInChildren<TextMesh>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                texts[i].text = "Photo Mode";
            }
        }

        internal static bool IsGameMenuButton(StartMenuButton button)
        {
            Transform parent = button.transform.parent;
            return parent != null && parent.name == GameMenuButtonName;
        }

        internal static void OpenFromMenu()
        {
            if (instance != null && !IsOpen)
            {
                instance.Open();
            }
        }

        private void Open()
        {
            Camera mainCamera = controller.UnityCamera;
            menu = menu != null ? menu : FindObjectOfType<StartMenu>();
            if (!GameState.playing || GameState.currentlyLoading || !GameState.inCursorMenu || GameState.currentShipyard != null
                || menu == null || mainCamera == null || Sun.sun == null || Weather.instance == null)
            {
                return;
            }

            settingsUi = SettingsUiField.GetValue(menu) as GameObject;
            Transform centerEye = controller.CenterEye;
            Transform uiCameraObject = centerEye != null ? centerEye.Find("UI camera") : null;
            uiCamera = uiCameraObject != null ? uiCameraObject.GetComponent<Camera>() : null;
            if (!EnsurePanel())
            {
                return;
            }

            logo = LogoField.GetValue(menu) as GameObject;
            playerModelMenuHide?.Invoke(null, null);
            settingsUiWasShown = settingsUi != null && settingsUi.activeSelf;
            if (settingsUiWasShown)
            {
                DisableSettingsMenuMethod.Invoke(menu, null);
            }

            logoWasShown = logo != null && logo.activeSelf;
            if (logoWasShown)
            {
                logo.SetActive(false);
            }

            menuPointer = menu.GetComponent<MouseButtonPointer>();
            menuPointerWasEnabled = menuPointer != null && menuPointer.enabled;
            if (menuPointer != null)
            {
                menuPointer.enabled = false;
            }

            sun = Sun.sun;
            sunLight = sun.GetComponent<Light>();
            sunlightIntensity = sun.GetComponent<SunlightNightIntensity>();
            savedGlobalTime = sun.globalTime;
            savedSunIntensity = sunLight != null ? sunLight.intensity : 0f;
            localTimeOffset = Mathf.DeltaAngle(0f, (sun.localTime - sun.globalTime) * 15f) / 15f;

            weather = Weather.instance;
            savedWeatherTarget = weather.targetWeatherSet;
            savedWeatherLerp = (float)WeatherLerpField.GetValue(weather);
            VolufogManager fog = VolufogManager.instance;
            if (fog != null)
            {
                savedFog[0] = fog.sunColorMult;
                savedFog[1] = fog.dayDensity;
                savedFog[2] = fog.daySaturaion;
                savedFog[3] = fog.dayMie;
                savedFog[4] = fog.moonColorMult;
                savedFog[5] = fog.nightDensity;
                savedFog[6] = fog.nightSaturation;
                savedFog[7] = fog.nightMie;
                savedDayFogMult = fog.dayDensityMult;
                savedNightFogMult = fog.nightDensityMult;
            }

            weatherParticles = new ParticleSystem[WeatherParticleFields.Length];
            savedParticleUnscaled = new bool[WeatherParticleFields.Length];
            for (int i = 0; i < WeatherParticleFields.Length; i++)
            {
                ParticleSystem particles = WeatherParticleFields[i]?.GetValue(weather) as ParticleSystem;
                weatherParticles[i] = particles;
                if (particles != null)
                {
                    ParticleSystem.MainModule main = particles.main;
                    savedParticleUnscaled[i] = main.useUnscaledTime;
                    main.useUnscaledTime = true;
                }
            }

            postProcessing = mainCamera.GetComponent<PostProcessingBehaviour>();
            originalProfile = postProcessing != null ? postProcessing.profile : null;
            if (originalProfile != null)
            {
                photoProfile = Instantiate(originalProfile);
                postProcessing.profile = photoProfile;
            }

            savedFieldOfView = mainCamera.fieldOfView;
            playerBody = FindPlayerBody();
            ResetControls();
            uiHidden = false;
            IsOpen = true;
            enabled = true;
            ShowPanel();
        }

        private void Close(bool showMenu)
        {
            if (!IsOpen)
            {
                return;
            }

            FreeCameraController.ExitActiveCamera();
            IsOpen = false;
            closedFrame = Time.frameCount;
            HidePanel();
            uiHidden = false;
            SetHudHidden(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            SetBodyForcedVisible(false);
            if (bodyHidden && playerBody != null)
            {
                playerBody.SetActive(true);
            }

            bodyHidden = false;
            if (postProcessing != null && originalProfile != null)
            {
                postProcessing.profile = originalProfile;
            }

            if (photoProfile != null)
            {
                Destroy(photoProfile);
                photoProfile = null;
            }

            if (controller.UnityCamera != null)
            {
                controller.UnityCamera.fieldOfView = savedFieldOfView;
            }

            sun.globalTime = savedGlobalTime;
            if (sunLight != null)
            {
                sunLight.intensity = savedSunIntensity;
            }

            RestoreWeather();
            for (int i = 0; i < weatherParticles.Length; i++)
            {
                if (weatherParticles[i] != null)
                {
                    ParticleSystem.MainModule main = weatherParticles[i].main;
                    main.useUnscaledTime = savedParticleUnscaled[i];
                }
            }

            VolufogManager fog = VolufogManager.instance;
            if (fog != null)
            {
                fog.dayDensityMult = savedDayFogMult;
                fog.nightDensityMult = savedNightFogMult;
            }

            if (menuPointer != null)
            {
                menuPointer.enabled = menuPointerWasEnabled;
            }

            if (showMenu)
            {
                if (logoWasShown && logo != null)
                {
                    logo.SetActive(true);
                }

                if (playerModelMenuReopen != null)
                {
                    playerModelMenuReopen.Invoke(null, null);
                }
                else if (settingsUiWasShown)
                {
                    EnableSettingsMenuMethod.Invoke(menu, null);
                }
            }

            enabled = false;
        }

        private void Update()
        {
            if (!IsOpen)
            {
                return;
            }

            // The pause ended another way (another mod resumed the game, or it is quitting).
            if (!GameState.playing || !GameState.inCursorMenu)
            {
                Close(false);
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10) || Input.GetKeyDown(KeyCode.JoystickButton6))
            {
                if (uiHidden)
                {
                    SetUiHidden(false);
                }
                else
                {
                    Close(true);
                }

                return;
            }

            if (!uiHidden)
            {
                UpdatePanel();
            }
        }

        // After the game's per-frame writers: the time's sunlight, the weather's fog set, the picture, the fog and the body.
        private void LateUpdate()
        {
            if (!IsOpen)
            {
                return;
            }

            if (time.Set && sunLight != null && sunlightIntensity != null)
            {
                // SunlightNightIntensity eases towards this with game time, which stands still while paused.
                sunLight.intensity = sun.dummySun.position.y < sun.nightLightOffHeight
                    ? 0f
                    : sunlightIntensity.dayIntensity * Mathf.InverseLerp(-25f, -1f, controller.CenterEye.position.y);
            }

            if (weatherFogDirty)
            {
                weatherFogDirty = false;
                ApplyWeatherFog();
            }

            ApplyPicture();
            if (fogDensity.Set)
            {
                RenderSettings.fogDensity *= fogDensity.Value;
            }

            SetBodyForcedVisible(FreeCameraController.IsFreeCameraOn && !BoatCamera.on);
        }

        private void OnApplicationQuit()
        {
            Close(false);
        }

        private void SetLocalTime(float localTime)
        {
            sun.globalTime = Mathf.Repeat(localTime - localTimeOffset, 24f);
            weatherFogDirty = true;
        }

        private void ResetTime()
        {
            sun.globalTime = savedGlobalTime;
            if (sunLight != null)
            {
                sunLight.intensity = savedSunIntensity;
            }

            weatherFogDirty = true;
        }

        // Preset 0 is the game's own weather; 1 to 4 are the region's clear, cloudy, rain and storm sets.
        private void SetWeatherPreset(int preset)
        {
            weatherPreset = preset;
            if (preset == 0)
            {
                RestoreWeather();
            }
            else
            {
                weather.targetWeatherSet = PresetSet(preset);
                WeatherLerpField.SetValue(weather, 1f);
            }

            weatherFogDirty = true;
        }

        private WeatherSet PresetSet(int preset)
        {
            Region region = weather.currentRegion;
            switch (preset)
            {
                case 1: return region.clearWeather;
                case 2: return region.cloudyWeather;
                case 3: return region.rainWeather;
                default: return region.stormWeather;
            }
        }

        private VolufogSet PresetFog(int preset)
        {
            Region region = weather.currentRegion;
            switch (preset)
            {
                case 1: return region.clearVolufog;
                case 2: return region.cloudyVolufog;
                case 3: return region.rainVolufog;
                default: return region.stormVolufog;
            }
        }

        // The volumetric fog values blend by the dawn value when set, so they follow a change of time or preset.
        private void ApplyWeatherFog()
        {
            VolufogManager fog = VolufogManager.instance;
            if (fog == null)
            {
                return;
            }

            if (weatherPreset != 0)
            {
                fog.ApplySet(PresetFog(weatherPreset));
            }
            else if (time.Set && WeatherStorms.instance != null)
            {
                // Weather.ChangeWeather is blocked while open, so this only recomputes the fog values for the new time.
                WeatherStorms.instance.ApplyStorm();
            }
            else
            {
                RestoreFogValues(fog);
            }
        }

        private void RestoreWeather()
        {
            weather.targetWeatherSet = savedWeatherTarget;
            WeatherLerpField.SetValue(weather, savedWeatherLerp);
            if (VolufogManager.instance != null)
            {
                RestoreFogValues(VolufogManager.instance);
            }
        }

        private void RestoreFogValues(VolufogManager fog)
        {
            fog.sunColorMult = savedFog[0];
            fog.dayDensity = savedFog[1];
            fog.daySaturaion = savedFog[2];
            fog.dayMie = savedFog[3];
            fog.moonColorMult = savedFog[4];
            fog.nightDensity = savedFog[5];
            fog.nightSaturation = savedFog[6];
            fog.nightMie = savedFog[7];
        }

        // The photo profile takes the game's current values from the original every frame, then the panel's.
        private void ApplyPicture()
        {
            if (photoProfile == null || originalProfile == null)
            {
                return;
            }

            ColorGradingModel.Settings grading = originalProfile.colorGrading.settings;
            grading.basic.postExposure += exposure.Set ? exposure.Value : 0f;
            grading.basic.contrast = contrast.Set ? contrast.Value : grading.basic.contrast;
            grading.basic.saturation = saturation.Set ? saturation.Value : grading.basic.saturation;
            grading.basic.temperature = temperature.Set ? temperature.Value : grading.basic.temperature;
            grading.basic.tint = tint.Set ? tint.Value : grading.basic.tint;
            photoProfile.colorGrading.enabled = originalProfile.colorGrading.enabled;
            photoProfile.colorGrading.settings = grading;

            BloomModel.Settings bloom = originalProfile.bloom.settings;
            bloom.bloom.intensity = bloomIntensity.Set ? bloomIntensity.Value : bloom.bloom.intensity;
            bloom.bloom.threshold = bloomThreshold.Set ? bloomThreshold.Value : bloom.bloom.threshold;
            photoProfile.bloom.enabled = originalProfile.bloom.enabled || bloomIntensity.Set;
            photoProfile.bloom.settings = bloom;

            VignetteModel.Settings vignette = originalProfile.vignette.settings;
            vignette.intensity = vignetteIntensity.Set ? vignetteIntensity.Value : vignette.intensity;
            vignette.smoothness = vignetteSmoothness.Set ? vignetteSmoothness.Value : vignette.smoothness;
            photoProfile.vignette.enabled = originalProfile.vignette.enabled || vignetteIntensity.Set;
            photoProfile.vignette.settings = vignette;

            GrainModel.Settings grain = originalProfile.grain.settings;
            grain.intensity = grainIntensity.Set ? grainIntensity.Value : grain.intensity;
            grain.size = grainSize.Set ? grainSize.Value : grain.size;
            photoProfile.grain.enabled = originalProfile.grain.enabled || grainIntensity.Set;
            photoProfile.grain.settings = grain;

            DepthOfFieldModel.Settings depth = originalProfile.depthOfField.settings;
            if (depthOfField)
            {
                depth.focusDistance = focusDistance;
                depth.aperture = aperture;
                depth.useCameraFov = true;
                depth.kernelSize = DepthOfFieldModel.KernelSize.Large;
            }

            photoProfile.depthOfField.enabled = originalProfile.depthOfField.enabled || depthOfField;
            photoProfile.depthOfField.settings = depth;

            ChromaticAberrationModel.Settings chromatic = originalProfile.chromaticAberration.settings;
            chromatic.intensity = chromaticAberration.Set ? chromaticAberration.Value : chromatic.intensity;
            photoProfile.chromaticAberration.enabled = originalProfile.chromaticAberration.enabled || chromaticAberration.Set;
            photoProfile.chromaticAberration.settings = chromatic;
        }

        private void SetVolumetricFog(float factor)
        {
            VolufogManager fog = VolufogManager.instance;
            if (fog != null)
            {
                fog.dayDensityMult = savedDayFogMult * factor;
                fog.nightDensityMult = savedNightFogMult * factor;
            }
        }

        private void SetFieldOfView(float fieldOfView)
        {
            if (FreeCameraController.IsFreeCameraOn)
            {
                controller.SetZoom(fieldOfView);
            }
            else if (controller.UnityCamera != null)
            {
                controller.UnityCamera.fieldOfView = fieldOfView;
            }
        }

        private void SetUiHidden(bool hidden)
        {
            uiHidden = hidden;
            SetHudHidden(hidden);
            Cursor.lockState = hidden ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !hidden;
        }

        // The UI camera draws the HUD on layer 5; the crosshair, look texts, money notification and buy popup are drawn by the
        // main camera, so their renderers are switched off one by one.
        private void SetHudHidden(bool hidden)
        {
            if (hidden == hudHidden)
            {
                return;
            }

            hudHidden = hidden;
            if (hidden)
            {
                if (uiCamera != null)
                {
                    savedUiCullingMask = uiCamera.cullingMask;
                    uiCamera.cullingMask = 0;
                }

                hudRenderers = CollectHudRenderers();
                savedHudRendererStates = new bool[hudRenderers.Length];
                for (int i = 0; i < hudRenderers.Length; i++)
                {
                    savedHudRendererStates[i] = hudRenderers[i].enabled;
                    hudRenderers[i].enabled = false;
                }

                return;
            }

            if (uiCamera != null)
            {
                uiCamera.cullingMask = savedUiCullingMask;
            }

            for (int i = 0; i < hudRenderers.Length; i++)
            {
                if (hudRenderers[i] != null)
                {
                    hudRenderers[i].enabled = savedHudRendererStates[i];
                }
            }

            hudRenderers = null;
            savedHudRendererStates = null;
        }

        private static Renderer[] CollectHudRenderers()
        {
            GameObject[] roots =
            {
                Refs.mouseCrosshair,
                GameObject.Find("look UI"),
                MoneyNotification.instance != null ? MoneyNotification.instance.gameObject : null,
                BuyItemUI.instance != null ? BuyItemUI.instance.gameObject : null
            };
            List<Renderer> renderers = new List<Renderer>();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] != null)
                {
                    renderers.AddRange(roots[i].GetComponentsInChildren<Renderer>(true));
                }
            }

            return renderers.ToArray();
        }

        private static GameObject FindPlayerBody()
        {
            object body = playerModelBody?.GetValue(null, null);
            PropertyInfo instanceProperty = body != null ? AccessTools.Property(body.GetType(), "Instance") : null;
            return instanceProperty != null ? instanceProperty.GetValue(body, null) as GameObject : null;
        }

        private void SetBodyHidden(bool hidden)
        {
            bodyHidden = hidden;
            if (playerBody != null)
            {
                playerBody.SetActive(!hidden);
            }
        }

        // Player Model draws the body only in third person; the free camera from first person shows it.
        private void SetBodyForcedVisible(bool visible)
        {
            if (visible == bodyForcedVisible || playerModelForceVisible == null)
            {
                return;
            }

            bodyForcedVisible = visible;
            playerModelForceVisible.Invoke(null, new object[] { visible });
        }

        // Photo mode's panel at the bottom right: copies of the game's settings page parts, built on the first opening and drawn by
        // the UI camera while photo mode is open and the UI is shown.
        private const float MinimumFieldOfView = 20f;
        private const float MinimumFocusDistance = 0.1f;
        private const float MaximumFocusDistance = 300f;
        private const string Hint = "B: free camera, look with the right mouse button held\nHide UI: ESC shows the panel again. ESC: leave";
        // The weather switches, for presets 1 to 4.
        private static readonly string[] WeatherNames = { "Clear", "Cloudy", "Rain", "Storm" };

        // Where the Photo Mode button sits in the game's pause menu, local to its settings page: under the version box.
        private static readonly Vector3 GameMenuButtonPosition = new Vector3(2.30f, -1.03f, -0.13f);

        // The layout, in the settings page's units; y runs down from the panel's top edge.
        private const float PanelWidth = 2.84f;
        private const float Padding = 0.14f;
        private const float RowHeight = 0.16f;
        private const float HeadingHeight = 0.2f;
        private const float ButtonRowHeight = 0.22f;
        private const float ButtonHeight = 0.15f;
        private const float Gap = 0.05f;
        private const float StripHeight = 0.13f;
        private const float BoxWidth = 0.15f;
        private const float BoxHeight = 0.13f;
        private const float LabelRight = 1.30f;
        private const float StripLeft = 1.38f;
        private const float StripWidth = 0.74f;
        private const float ValueRight = 2.50f;
        private const float ResetCentre = 2.625f;
        private const float HintHeight = 0.26f;
        private const float PaperMargin = 0.08f;
        private const float PaperDepth = 0.16f;
        private const float TextDepth = -0.01f;
        private const float SmallText = 0.85f;
        private const int BarCount = 20;

        // Held in front of the UI camera at the page's distance, at this share of the page's size (smaller if it would not fit
        // the view's height), with this gap to the screen's edges as a share of the view's half height.
        private const float PanelDistance = 3f;
        private const float PanelScale = 0.72f;
        private const float ScreenMargin = 0.04f;
        private static readonly Color PaperEmission = new Color(0.85f, 0.85f, 0.85f, 1f);
        private const float DimAlpha = 0.35f;

        // A panel value that follows the game's until it is moved; Reset makes it follow again.
        private struct Setting
        {
            public bool Set;
            public float Value;
        }

        private enum SliderId
        {
            Time, FieldOfView, FocusDistance, Aperture, Exposure, Contrast, Saturation, Temperature, Tint, BloomIntensity,
            BloomThreshold, Vignette, VignetteSoftness, FilmGrain, GrainSize, ChromaticAberration, Fog, VolumetricFog
        }

        private enum Part { HideUi, Leave, Weather, DepthOfField, HideBody, Grid, ResetAll, Slider, Reset }

        // A pressable area, in panel units, and the outline of its plate, shown while the pointer is on it.
        private struct Hit
        {
            public Rect Area;
            public Part Part;
            public int Index;
            public cakeslice.Outline Outline;
        }

        private sealed class SliderRow
        {
            public SliderId Id;
            public TextMesh Label;
            public TextMesh Bars;
            public TextMesh Value;
            public Renderer ResetPlate;
            public TextMesh ResetArrow;
            public float BarsLeft;
            public float BarsWidth;
            public float ShownValue;
            public float ShownFraction;
            public int State;
        }

        // The settings page's parts the panel is copied from, held only while it is built.
        private sealed class PanelParts
        {
            public Transform Page;
            public Transform Paper;
            public Transform Plate;
            public Transform PlateText;
            public Transform Mark;
            public Transform Label;
            public Transform Heading;
            public Transform Value;
            public Transform Strip;
            public Transform Bars;
            public Transform Arrow;
            public float MarkOffset;
            public float BarsInset;
        }

        private Setting time;
        private Setting fieldOfView;
        private Setting exposure;
        private Setting contrast;
        private Setting saturation;
        private Setting temperature;
        private Setting tint;
        private Setting bloomIntensity;
        private Setting bloomThreshold;
        private Setting vignetteIntensity;
        private Setting vignetteSmoothness;
        private Setting grainIntensity;
        private Setting grainSize;
        private Setting chromaticAberration;
        private Setting fogDensity;
        private Setting volumetricFog;
        private int weatherPreset;
        private bool depthOfField;
        private float focusDistance;
        private float aperture;
        private bool showGrid;
        private bool mouseOverPanel;

        private Transform panelRoot;
        private float panelHeight;
        private Rect paperArea;
        private readonly List<Hit> hits = new List<Hit>();
        private SliderRow[] sliderRows;
        private Renderer[] weatherPlates;
        private TextMesh depthOfFieldMark;
        private TextMesh hideBodyMark;
        private TextMesh gridMark;
        private TextMesh hideBodyLabel;
        private Renderer hideBodyPlate;
        private Material plateOn;
        private Material plateOff;
        private Color ink;
        private Color barColour;
        private Color plateInk;
        private readonly string[] barTexts = new string[BarCount + 1];
        private int hovered = -1;
        private SliderRow dragged;
        private int shownPreset;
        private int shownChecks;
        private int layoutWidth;
        private int layoutHeight;

        private float FieldOfViewMaximum
        {
            get
            {
                return FreeCameraController.IsFreeCameraOn ? controller.MaximumZoom : savedFieldOfView;
            }
        }

        private void ResetControls()
        {
            time = fieldOfView = exposure = contrast = saturation = temperature = tint = default(Setting);
            bloomIntensity = bloomThreshold = vignetteIntensity = vignetteSmoothness = default(Setting);
            grainIntensity = grainSize = chromaticAberration = fogDensity = volumetricFog = default(Setting);
            weatherPreset = 0;
            depthOfField = false;
            focusDistance = 10f;
            aperture = 5.6f;
            showGrid = false;
        }

        private void ResetAll()
        {
            if (time.Set)
            {
                ResetTime();
            }

            if (weatherPreset != 0)
            {
                SetWeatherPreset(0);
            }

            SetVolumetricFog(1f);
            SetFieldOfView(FieldOfViewMaximum);
            if (bodyHidden)
            {
                SetBodyHidden(false);
            }

            ResetControls();
        }

        // Builds the panel once from the settings page; false when a part is missing.
        private bool EnsurePanel()
        {
            if (panelRoot != null)
            {
                return true;
            }

            if (uiCamera == null || settingsUi == null || AutosaveUI.instance == null)
            {
                return false;
            }

            Transform page = settingsUi.transform;
            PanelParts parts = new PanelParts
            {
                Page = page,
                Paper = page.Find("bg"),
                Plate = page.Find("switch"),
                PlateText = page.Find("switch/text (1)"),
                Mark = page.Find("chekbox (5)/text"),
                Label = page.Find("text (3)"),
                Heading = page.Find("text (2)"),
                Value = page.Find("text fov"),
                Strip = page.Find("slider fov"),
                Bars = page.Find("slider fov/text volume"),
                Arrow = page.Find("button back/text (1)")
            };
            GPButtonSliderVolume slider = parts.Strip != null ? parts.Strip.GetComponent<GPButtonSliderVolume>() : null;
            if (parts.Paper == null || parts.Plate == null || parts.PlateText == null || parts.Mark == null || parts.Label == null
                || parts.Heading == null || parts.Value == null || slider == null || parts.Bars == null || parts.Arrow == null
                || AutosaveUI.instance.onMaterial == null || AutosaveUI.instance.offMaterial == null)
            {
                plugin.LogInitializationError("Photo mode: a part of the game's settings page it copies is missing.");
                return false;
            }

            parts.MarkOffset = page.InverseTransformPoint(parts.Mark.position).y - page.InverseTransformPoint(parts.Mark.parent.position).y;
            parts.BarsInset = page.InverseTransformPoint(parts.Bars.position).x
                - (page.InverseTransformPoint(parts.Strip.position).x - PageScale(parts, parts.Strip).x * 0.5f);
            plateOn = AutosaveUI.instance.onMaterial;
            plateOff = AutosaveUI.instance.offMaterial;
            ink = parts.Label.GetComponent<TextMesh>().color;
            barColour = parts.Bars.GetComponent<TextMesh>().color;
            plateInk = parts.PlateText.GetComponent<TextMesh>().color;
            string bar = string.IsNullOrEmpty(slider.bar) ? "▋" : slider.bar;
            for (int i = 0; i <= BarCount; i++)
            {
                barTexts[i] = i == 0 ? string.Empty : barTexts[i - 1] + bar;
            }

            // Built under an inactive root, so the copies' game scripts never wake before they are removed.
            GameObject root = new GameObject("freecam photo panel");
            root.SetActive(false);
            root.layer = page.gameObject.layer;
            panelRoot = root.transform;
            panelRoot.SetParent(uiCamera.transform, false);
            hits.Clear();

            float y = -Padding - ButtonRowHeight * 0.5f;
            float buttonWidth = (PanelWidth - 2f * Padding - Gap) * 0.5f;
            AddButton(parts, Part.HideUi, "Hide UI", Padding + buttonWidth * 0.5f, y, buttonWidth);
            AddButton(parts, Part.Leave, "Leave", PanelWidth - Padding - buttonWidth * 0.5f, y, buttonWidth);
            y -= ButtonRowHeight * 0.5f;

            List<SliderRow> rows = new List<SliderRow>();
            AddHeading(parts, "SCENE", ref y);
            rows.Add(AddSlider(parts, SliderId.Time, "time of day", rows.Count, ref y));
            y -= ButtonRowHeight * 0.5f;
            int weatherCount = WeatherNames.Length;
            float weatherWidth = (PanelWidth - 2f * Padding - (weatherCount - 1) * Gap) / weatherCount;
            weatherPlates = new Renderer[weatherCount];
            for (int i = 0; i < weatherCount; i++)
            {
                weatherPlates[i] = AddButton(parts, Part.Weather, WeatherNames[i], Padding + weatherWidth * 0.5f + i * (weatherWidth + Gap), y, weatherWidth, i + 1);
            }

            y -= ButtonRowHeight * 0.5f;
            AddHeading(parts, "CAMERA", ref y);
            rows.Add(AddSlider(parts, SliderId.FieldOfView, "field of view", rows.Count, ref y));
            depthOfFieldMark = AddCheckbox(parts, Part.DepthOfField, "depth of field", ref y, out _, out _);
            rows.Add(AddSlider(parts, SliderId.FocusDistance, "focus distance", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.Aperture, "aperture", rows.Count, ref y));

            AddHeading(parts, "PICTURE", ref y);
            rows.Add(AddSlider(parts, SliderId.Exposure, "exposure", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.Contrast, "contrast", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.Saturation, "saturation", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.Temperature, "temperature", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.Tint, "tint", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.BloomIntensity, "bloom", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.BloomThreshold, "bloom threshold", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.Vignette, "vignette", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.VignetteSoftness, "vignette softness", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.FilmGrain, "film grain", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.GrainSize, "grain size", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.ChromaticAberration, "chromatic aberration", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.Fog, "fog", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.VolumetricFog, "volumetric fog", rows.Count, ref y));
            sliderRows = rows.ToArray();

            AddHeading(parts, "EXTRAS", ref y);
            hideBodyMark = AddCheckbox(parts, Part.HideBody, "hide the player's body", ref y, out hideBodyLabel, out hideBodyPlate);
            gridMark = AddCheckbox(parts, Part.Grid, "framing grid", ref y, out _, out _);
            y -= ButtonRowHeight * 0.5f;
            AddButton(parts, Part.ResetAll, "Reset All", PanelWidth * 0.5f, y, buttonWidth);
            y -= ButtonRowHeight * 0.5f + Gap;
            AddText(parts, parts.Label, "hint", Hint, PanelWidth * 0.5f, y, TextAnchor.UpperCenter, SmallText).alignment = TextAlignment.Center;
            y -= HintHeight + Padding;
            panelHeight = -y;
            AddPaper(parts);
            return true;
        }

        private static Vector3 PageScale(PanelParts parts, Transform part)
        {
            Vector3 page = parts.Page.lossyScale;
            Vector3 scale = part.lossyScale;
            return new Vector3(scale.x / page.x, scale.y / page.y, scale.z / page.z);
        }

        // A copy of a page part under the panel, at the part's own turn and size in page units, without its children, button
        // scripts and colliders.
        private GameObject Copy(PanelParts parts, Transform source, string name, float x, float y, float z)
        {
            GameObject copy = Instantiate(source.gameObject, panelRoot);
            copy.name = name;
            Transform transform = copy.transform;
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(transform.GetChild(i).gameObject);
            }

            foreach (GoPointerButton button in copy.GetComponents<GoPointerButton>())
            {
                DestroyImmediate(button);
            }

            foreach (Collider collider in copy.GetComponents<Collider>())
            {
                DestroyImmediate(collider);
            }

            cakeslice.Outline outline = copy.GetComponent<cakeslice.Outline>();
            if (outline != null)
            {
                outline.enabled = false;
            }

            transform.localRotation = Quaternion.Inverse(parts.Page.rotation) * source.rotation;
            transform.localScale = PageScale(parts, source);
            transform.localPosition = new Vector3(x, y, z);
            copy.SetActive(true);
            return copy;
        }

        private TextMesh AddText(PanelParts parts, Transform source, string name, string text, float x, float y, TextAnchor anchor, float size = 1f)
        {
            TextMesh mesh = Copy(parts, source, name, x, y, TextDepth).GetComponent<TextMesh>();
            mesh.anchor = anchor;
            mesh.text = text;
            mesh.transform.localScale *= size;
            return mesh;
        }

        private Renderer AddPlate(PanelParts parts, Transform source, string name, float x, float y, float width, float height)
        {
            GameObject plate = Copy(parts, source, name, x, y, 0f);
            Vector3 scale = plate.transform.localScale;
            plate.transform.localScale = new Vector3(width, height, scale.z);
            return plate.GetComponent<Renderer>();
        }

        private void AddHit(Part part, int index, float x, float y, float width, float height, Renderer plate)
        {
            // The game adds a button's outline in its first Start, so the parts of a page never shown have none.
            cakeslice.Outline outline = plate.GetComponent<cakeslice.Outline>();
            if (outline == null)
            {
                outline = plate.gameObject.AddComponent<cakeslice.Outline>();
                outline.enabled = false;
            }

            hits.Add(new Hit
            {
                Area = new Rect(x - width * 0.5f, y - height * 0.5f, width, height),
                Part = part,
                Index = index,
                Outline = outline
            });
        }

        private Renderer AddButton(PanelParts parts, Part part, string text, float x, float y, float width, int index = 0)
        {
            Renderer plate = AddPlate(parts, parts.Plate, (part == Part.Weather ? "weather " : "button ") + text, x, y, width, ButtonHeight);
            plate.sharedMaterial = plateOn;
            AddText(parts, parts.PlateText, "text " + text, text, x, y, TextAnchor.MiddleCenter);
            AddHit(part, index, x, y, width, ButtonHeight, plate);
            return plate;
        }

        private void AddHeading(PanelParts parts, string text, ref float y)
        {
            y -= HeadingHeight * 0.5f;
            AddText(parts, parts.Heading, "heading " + text, text, PanelWidth * 0.5f, y, TextAnchor.MiddleCenter);
            y -= HeadingHeight * 0.5f;
        }

        private TextMesh AddCheckbox(PanelParts parts, Part part, string label, ref float y, out TextMesh labelText, out Renderer box)
        {
            y -= RowHeight * 0.5f;
            labelText = AddText(parts, parts.Label, "label " + label, label, LabelRight, y, TextAnchor.MiddleRight);
            float x = StripLeft + BoxWidth * 0.5f;
            box = AddPlate(parts, parts.Plate, "checkbox " + label, x, y, BoxWidth, BoxHeight);
            box.sharedMaterial = plateOn;
            TextMesh mark = AddText(parts, parts.Mark, "mark " + label, string.Empty, x, y + parts.MarkOffset, TextAnchor.MiddleCenter);
            AddHit(part, 0, x, y, BoxWidth, BoxHeight, box);
            y -= RowHeight * 0.5f;
            return mark;
        }

        private SliderRow AddSlider(PanelParts parts, SliderId id, string label, int index, ref float y)
        {
            y -= RowHeight * 0.5f;
            SliderRow row = new SliderRow { Id = id };
            row.Label = AddText(parts, parts.Label, "label " + label, label, LabelRight, y, TextAnchor.MiddleRight);
            float stripX = StripLeft + StripWidth * 0.5f;
            Renderer strip = AddPlate(parts, parts.Strip, "slider " + label, stripX, y, StripWidth, StripHeight);
            float squeeze = StripWidth / PageScale(parts, parts.Strip).x;
            row.BarsLeft = StripLeft + parts.BarsInset * squeeze;
            row.BarsWidth = StripWidth - 2f * parts.BarsInset * squeeze;
            row.Bars = AddText(parts, parts.Bars, "bars " + label, string.Empty, row.BarsLeft, y, TextAnchor.MiddleLeft);
            Vector3 barsScale = row.Bars.transform.localScale;
            row.Bars.transform.localScale = new Vector3(barsScale.x * squeeze, barsScale.y, barsScale.z);
            row.Value = AddText(parts, parts.Value, "value " + label, string.Empty, ValueRight, y, TextAnchor.MiddleRight);
            AddHit(Part.Slider, index, stripX, y, StripWidth, RowHeight, strip);
            if (id != SliderId.FocusDistance && id != SliderId.Aperture)
            {
                row.ResetPlate = AddPlate(parts, parts.Plate, "reset " + label, ResetCentre, y, BoxWidth, BoxHeight);
                row.ResetArrow = AddText(parts, parts.Arrow, "arrow " + label, "↩", ResetCentre, y, TextAnchor.MiddleCenter, 0.8f);
                AddHit(Part.Reset, index, ResetCentre, y, BoxWidth, BoxHeight, row.ResetPlate);
            }

            y -= RowHeight * 0.5f;
            return row;
        }

        // The page's paper stretched behind the panel, its texture drawn as emission so the light of the time of day photo mode
        // sets doesn't darken it.
        private void AddPaper(PanelParts parts)
        {
            float width = PanelWidth + 2f * PaperMargin;
            float height = panelHeight + 2f * PaperMargin;
            paperArea = new Rect(-PaperMargin, -panelHeight - PaperMargin, width, height);
            GameObject paper = Copy(parts, parts.Paper, "paper", 0f, 0f, 0f);
            Bounds bounds = paper.GetComponent<MeshFilter>().sharedMesh.bounds;
            Transform transform = paper.transform;
            Vector3 scale = transform.localScale;
            transform.localScale = new Vector3(width / bounds.size.x, height / bounds.size.y, scale.z);
            Vector3 centre = new Vector3(PanelWidth * 0.5f, -panelHeight * 0.5f, PaperDepth);
            transform.localPosition = centre - transform.localRotation * Vector3.Scale(bounds.center, transform.localScale);
            Renderer renderer = paper.GetComponent<Renderer>();
            Material material = new Material(renderer.sharedMaterial) { name = "freecam photo panel paper" };
            material.SetTexture("_EmissionMap", material.mainTexture);
            material.SetColor("_EmissionColor", PaperEmission);
            material.EnableKeyword("_EMISSION");
            material.color = Color.black;
            renderer.sharedMaterial = material;
        }

        private void ShowPanel()
        {
            panelRoot.gameObject.SetActive(true);
            PlacePanel();
            for (int i = 0; i < sliderRows.Length; i++)
            {
                sliderRows[i].ShownValue = float.NaN;
                sliderRows[i].ShownFraction = float.NaN;
                sliderRows[i].State = -1;
            }

            shownPreset = -1;
            shownChecks = -1;
            RefreshPanel();
        }

        private void HidePanel()
        {
            SetHovered(-1);
            dragged = null;
            mouseOverPanel = false;
            if (panelRoot != null)
            {
                panelRoot.gameObject.SetActive(false);
            }
        }

        // The panel's top left corner placed so it ends at the view's bottom right corner.
        private void PlacePanel()
        {
            layoutWidth = Screen.width;
            layoutHeight = Screen.height;
            float halfHeight = Mathf.Tan(uiCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) * PanelDistance;
            float halfWidth = halfHeight * uiCamera.aspect;
            float margin = halfHeight * ScreenMargin;
            float pageUnit = settingsUi.transform.lossyScale.x / uiCamera.transform.lossyScale.x;
            float paperHeight = panelHeight + 2f * PaperMargin;
            float unit = pageUnit * Mathf.Min(PanelScale, (2f * halfHeight - 2f * margin) / (pageUnit * paperHeight));
            panelRoot.localScale = new Vector3(unit, unit, unit);
            panelRoot.localRotation = Quaternion.identity;
            panelRoot.localPosition = new Vector3(halfWidth - margin - (PanelWidth + PaperMargin) * unit, -halfHeight + margin + (panelHeight + PaperMargin) * unit, PanelDistance);
        }

        private void UpdatePanel()
        {
            if (Screen.width != layoutWidth || Screen.height != layoutHeight)
            {
                PlacePanel();
            }

            HandlePointer(Input.mousePosition, Input.GetMouseButtonDown(0) && !Input.GetMouseButton(1), Input.GetMouseButton(0));
            if (IsPanelShown)
            {
                RefreshPanel();
            }
        }

        // The pointer on the panel: the hovered plate's outline, a press on a control, a slider following a held button.
        private void HandlePointer(Vector2 screenPosition, bool pressed, bool held)
        {
            Ray ray = uiCamera.ScreenPointToRay(screenPosition);
            Plane plane = new Plane(panelRoot.forward, panelRoot.position);
            bool onPlane = plane.Raycast(ray, out float distance);
            Vector2 point = onPlane ? (Vector2)panelRoot.InverseTransformPoint(ray.GetPoint(distance)) : Vector2.zero;
            if (dragged != null)
            {
                if (held && onPlane)
                {
                    SetSlider(dragged.Id, FromFraction(dragged.Id, (point.x - dragged.BarsLeft) / dragged.BarsWidth));
                    return;
                }

                dragged = null;
            }

            mouseOverPanel = onPlane && paperArea.Contains(point);
            int over = -1;
            if (mouseOverPanel)
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    if (hits[i].Area.Contains(point) && CanPress(hits[i]))
                    {
                        over = i;
                        break;
                    }
                }
            }

            SetHovered(over);
            if (pressed && over >= 0)
            {
                if (UISoundPlayer.instance != null)
                {
                    UISoundPlayer.instance.PlayUIClickSound();
                }

                Press(hits[over], point);
            }
        }

        private bool CanPress(Hit hit)
        {
            switch (hit.Part)
            {
                case Part.HideBody:
                    return playerBody != null;
                case Part.Slider:
                    return RowEnabled(sliderRows[hit.Index].Id);
                case Part.Reset:
                    return SettingOf(sliderRows[hit.Index].Id).Set;
                default:
                    return true;
            }
        }

        private void Press(Hit hit, Vector2 point)
        {
            switch (hit.Part)
            {
                case Part.HideUi:
                    SetHovered(-1);
                    SetUiHidden(true);
                    break;
                case Part.Leave:
                    Close(true);
                    break;
                case Part.Weather:
                    // The lit switch pressed again gives the game's own weather back.
                    SetWeatherPreset(hit.Index == weatherPreset ? 0 : hit.Index);
                    break;
                case Part.DepthOfField:
                    depthOfField = !depthOfField;
                    break;
                case Part.HideBody:
                    SetBodyHidden(!bodyHidden);
                    break;
                case Part.Grid:
                    showGrid = !showGrid;
                    break;
                case Part.ResetAll:
                    ResetAll();
                    break;
                case Part.Slider:
                    dragged = sliderRows[hit.Index];
                    SetSlider(dragged.Id, FromFraction(dragged.Id, (point.x - dragged.BarsLeft) / dragged.BarsWidth));
                    break;
                case Part.Reset:
                    ResetSlider(sliderRows[hit.Index].Id);
                    break;
            }
        }

        private void SetHovered(int index)
        {
            if (index == hovered)
            {
                return;
            }

            if (hovered >= 0 && hovered < hits.Count)
            {
                hits[hovered].Outline.enabled = false;
            }

            hovered = index;
            if (index < 0)
            {
                return;
            }

            hits[index].Outline.color = 1;
            hits[index].Outline.enabled = true;

            if (UISoundPlayer.instance != null)
            {
                UISoundPlayer.instance.PlayUISound(UISounds.buttonHover, 0.6f, 1f);
            }
        }

        // Each row's texts and plates change only when what they show changed.
        private void RefreshPanel()
        {
            for (int i = 0; i < sliderRows.Length; i++)
            {
                SliderRow row = sliderRows[i];
                float shown = ShownValue(row.Id);
                float fraction = Fraction(row.Id, shown);
                if (shown != row.ShownValue)
                {
                    row.ShownValue = shown;
                    row.Value.text = Format(row.Id, shown);
                }

                if (fraction != row.ShownFraction)
                {
                    row.ShownFraction = fraction;
                    row.Bars.text = barTexts[Mathf.RoundToInt(fraction * BarCount)];
                }

                bool enabledRow = RowEnabled(row.Id);
                bool resettable = row.ResetPlate != null && SettingOf(row.Id).Set;
                int state = (enabledRow ? 1 : 0) | (resettable ? 2 : 0);
                if (state != row.State)
                {
                    row.State = state;
                    row.Label.color = Faded(ink, enabledRow);
                    row.Value.color = Faded(ink, enabledRow);
                    row.Bars.color = Faded(barColour, enabledRow);
                    if (row.ResetPlate != null)
                    {
                        row.ResetPlate.sharedMaterial = resettable ? plateOn : plateOff;
                        row.ResetArrow.color = Faded(plateInk, resettable);
                    }
                }
            }

            if (weatherPreset != shownPreset)
            {
                shownPreset = weatherPreset;
                for (int i = 0; i < weatherPlates.Length; i++)
                {
                    weatherPlates[i].sharedMaterial = i + 1 == weatherPreset ? plateOn : plateOff;
                }
            }

            int checks = (depthOfField ? 1 : 0) | (bodyHidden ? 2 : 0) | (showGrid ? 4 : 0) | (playerBody != null ? 8 : 0);
            if (checks != shownChecks)
            {
                shownChecks = checks;
                depthOfFieldMark.text = depthOfField ? "X" : string.Empty;
                hideBodyMark.text = bodyHidden ? "X" : string.Empty;
                gridMark.text = showGrid ? "X" : string.Empty;
                hideBodyLabel.color = Faded(ink, playerBody != null);
                hideBodyPlate.sharedMaterial = playerBody != null ? plateOn : plateOff;
            }
        }

        private static Color Faded(Color colour, bool full)
        {
            return full ? colour : new Color(colour.r, colour.g, colour.b, colour.a * DimAlpha);
        }

        private bool RowEnabled(SliderId id)
        {
            return depthOfField || (id != SliderId.FocusDistance && id != SliderId.Aperture);
        }

        // The settings by slider, by reference; focus distance and aperture are plain values and use time's slot only as a
        // placeholder (their rows have no reset).
        private ref Setting SettingOf(SliderId id)
        {
            switch (id)
            {
                case SliderId.FieldOfView: return ref fieldOfView;
                case SliderId.Exposure: return ref exposure;
                case SliderId.Contrast: return ref contrast;
                case SliderId.Saturation: return ref saturation;
                case SliderId.Temperature: return ref temperature;
                case SliderId.Tint: return ref tint;
                case SliderId.BloomIntensity: return ref bloomIntensity;
                case SliderId.BloomThreshold: return ref bloomThreshold;
                case SliderId.Vignette: return ref vignetteIntensity;
                case SliderId.VignetteSoftness: return ref vignetteSmoothness;
                case SliderId.FilmGrain: return ref grainIntensity;
                case SliderId.GrainSize: return ref grainSize;
                case SliderId.ChromaticAberration: return ref chromaticAberration;
                case SliderId.Fog: return ref fogDensity;
                case SliderId.VolumetricFog: return ref volumetricFog;
                default: return ref time;
            }
        }

        private float ShownValue(SliderId id)
        {
            if (id == SliderId.FocusDistance)
            {
                return focusDistance;
            }

            if (id == SliderId.Aperture)
            {
                return aperture;
            }

            Setting setting = SettingOf(id);
            return setting.Set ? setting.Value : GameValue(id);
        }

        // The value a slider shows while it follows the game.
        private float GameValue(SliderId id)
        {
            switch (id)
            {
                case SliderId.Time:
                    return Mathf.Repeat(sun.localTime, 24f);
                case SliderId.FieldOfView:
                    return controller.UnityCamera != null ? controller.UnityCamera.fieldOfView : savedFieldOfView;
                case SliderId.Exposure:
                    return 0f;
                case SliderId.Fog:
                case SliderId.VolumetricFog:
                    return 1f;
            }

            if (originalProfile == null)
            {
                return 0f;
            }

            switch (id)
            {
                case SliderId.Contrast: return originalProfile.colorGrading.settings.basic.contrast;
                case SliderId.Saturation: return originalProfile.colorGrading.settings.basic.saturation;
                case SliderId.Temperature: return originalProfile.colorGrading.settings.basic.temperature;
                case SliderId.Tint: return originalProfile.colorGrading.settings.basic.tint;
                case SliderId.BloomIntensity: return originalProfile.bloom.settings.bloom.intensity;
                case SliderId.BloomThreshold: return originalProfile.bloom.settings.bloom.threshold;
                case SliderId.Vignette: return originalProfile.vignette.settings.intensity;
                case SliderId.VignetteSoftness: return originalProfile.vignette.settings.smoothness;
                case SliderId.FilmGrain: return originalProfile.grain.settings.intensity;
                case SliderId.GrainSize: return originalProfile.grain.settings.size;
                default: return originalProfile.chromaticAberration.settings.intensity;
            }
        }

        private void Range(SliderId id, out float minimum, out float maximum)
        {
            minimum = 0f;
            switch (id)
            {
                case SliderId.Time: maximum = 24f; return;
                case SliderId.FieldOfView: minimum = MinimumFieldOfView; maximum = FieldOfViewMaximum; return;
                case SliderId.FocusDistance: minimum = MinimumFocusDistance; maximum = MaximumFocusDistance; return;
                case SliderId.Aperture: minimum = 1.4f; maximum = 22f; return;
                case SliderId.Exposure: minimum = -2f; maximum = 2f; return;
                case SliderId.Temperature:
                case SliderId.Tint: minimum = -100f; maximum = 100f; return;
                case SliderId.BloomIntensity:
                case SliderId.Fog:
                case SliderId.VolumetricFog: maximum = 3f; return;
                case SliderId.Contrast:
                case SliderId.Saturation:
                case SliderId.BloomThreshold: maximum = 2f; return;
                case SliderId.VignetteSoftness: minimum = 0.01f; maximum = 1f; return;
                case SliderId.GrainSize: minimum = 0.3f; maximum = 3f; return;
                default: maximum = 1f; return;
            }
        }

        // Focus distance runs on a logarithmic scale; the others are linear.
        private float Fraction(SliderId id, float value)
        {
            Range(id, out float minimum, out float maximum);
            return id == SliderId.FocusDistance
                ? Mathf.InverseLerp(Mathf.Log10(minimum), Mathf.Log10(maximum), Mathf.Log10(Mathf.Max(value, minimum)))
                : Mathf.InverseLerp(minimum, maximum, value);
        }

        private float FromFraction(SliderId id, float fraction)
        {
            Range(id, out float minimum, out float maximum);
            fraction = Mathf.Clamp01(fraction);
            return id == SliderId.FocusDistance
                ? Mathf.Pow(10f, Mathf.Lerp(Mathf.Log10(minimum), Mathf.Log10(maximum), fraction))
                : Mathf.Lerp(minimum, maximum, fraction);
        }

        private void SetSlider(SliderId id, float value)
        {
            if (id == SliderId.FocusDistance)
            {
                focusDistance = value;
                return;
            }

            if (id == SliderId.Aperture)
            {
                aperture = value;
                return;
            }

            ref Setting setting = ref SettingOf(id);
            setting.Set = true;
            setting.Value = value;
            ApplySetting(id, setting);
        }

        private void ResetSlider(SliderId id)
        {
            ref Setting setting = ref SettingOf(id);
            setting = default(Setting);
            ApplySetting(id, setting);
        }

        // The settings the game doesn't read from the photo profile each frame.
        private void ApplySetting(SliderId id, Setting setting)
        {
            switch (id)
            {
                case SliderId.Time:
                    if (setting.Set)
                    {
                        SetLocalTime(setting.Value);
                    }
                    else
                    {
                        ResetTime();
                    }

                    break;
                case SliderId.FieldOfView:
                    SetFieldOfView(setting.Set ? setting.Value : FieldOfViewMaximum);
                    break;
                case SliderId.VolumetricFog:
                    SetVolumetricFog(setting.Set ? setting.Value : 1f);
                    break;
            }
        }

        private static string Format(SliderId id, float value)
        {
            switch (id)
            {
                case SliderId.Time: return FormatTime(value);
                case SliderId.FieldOfView: return value.ToString("0");
                case SliderId.FocusDistance: return value.ToString(value < 10f ? "0.0" : "0") + " m";
                case SliderId.Aperture: return "f/" + value.ToString("0.0");
                case SliderId.Exposure: return value.ToString("+0.0;-0.0;0.0");
                case SliderId.Temperature:
                case SliderId.Tint: return value.ToString("0");
                case SliderId.Fog:
                case SliderId.VolumetricFog: return "x" + value.ToString("0.00");
                default: return value.ToString("0.00");
            }
        }

        private static string FormatTime(float hours)
        {
            int minutes = Mathf.FloorToInt(Mathf.Repeat(hours, 24f) * 60f);
            return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
        }

        // Rule-of-thirds lines, with the panel.
        private void OnGUI()
        {
            if (!showGrid || !IsPanelShown || Event.current.type != EventType.Repaint)
            {
                return;
            }

            float width = Screen.width;
            float height = Screen.height;
            float line = Mathf.Max(1f, Mathf.Round(height / 1080f));
            Color color = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.45f);
            for (int i = 1; i < 3; i++)
            {
                GUI.DrawTexture(new Rect(width * i / 3f, 0f, line, height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(0f, height * i / 3f, width, line), Texture2D.whiteTexture);
            }

            GUI.color = color;
        }
    }

    // StartMenu.LateUpdate is the game's ESC handling; it is skipped while photo mode has the key.
    [HarmonyPatch(typeof(StartMenu), "LateUpdate")]
    internal static class StartMenuLateUpdatePatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !PhotoMode.ClaimsPauseKey;
        }
    }

    // Without Player Model's pause menu, the Photo Mode button is added to the game's pause menu when it opens.
    [HarmonyPatch(typeof(StartMenu), "GameToSettings")]
    internal static class StartMenuGameToSettingsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(StartMenu __instance)
        {
            if (!PhotoMode.UsesPlayerModelMenu)
            {
                PhotoMode.EnsureGameMenuButton(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(StartMenuButton), "OnActivate")]
    internal static class StartMenuButtonOnActivatePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(StartMenuButton __instance)
        {
            if (!PhotoMode.IsGameMenuButton(__instance))
            {
                return true;
            }

            PhotoMode.OpenFromMenu();
            return false;
        }
    }

    // The weather stays as photo mode sets it while photo mode is open.
    [HarmonyPatch(typeof(Weather), "ChangeWeather")]
    internal static class WeatherChangeWeatherPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !PhotoMode.IsOpen;
        }
    }
}
