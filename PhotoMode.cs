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
        private static readonly FieldInfo StormsField = AccessTools.Field(typeof(WeatherStorms), "storms");
        private static readonly FieldInfo CurrentStormField = AccessTools.Field(typeof(WeatherStorms), "currentStorm");
        private static readonly FieldInfo BlendedSetField = AccessTools.Field(typeof(WeatherStorms), "blendedSet");
        private static readonly FieldInfo StormPlayerField = AccessTools.Field(typeof(WeatherStorms), "player");
        private static readonly FieldInfo BottomParticlesField = AccessTools.Field(typeof(WanderingStorm), "bottomParticles");
        private static readonly MethodInfo LightningStrikeMethod = AccessTools.Method(typeof(WanderingStormLightning), "LightningStrike");
        private static readonly FieldInfo LightTimerField = AccessTools.Field(typeof(WanderingStormLightning), "lightTimer");
        private static readonly FieldInfo CooldownField = AccessTools.Field(typeof(WanderingStormLightning), "lightningCooldown");
        private static readonly int ExposureId = Shader.PropertyToID("_Exposure");
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
        // Every storm's particle systems and Better Storm's rain, sand and snow, on real time while open, with what each had.
        private readonly List<ParticleSystem> stormParticles = new List<ParticleSystem>();
        private readonly List<bool> stormParticlesUnscaled = new List<bool>();
        private float nextParticleScan;
        private bool weatherFogDirty;
        private WanderingStorm[] storms;
        private string[] stormNames;

        // The summoned storm and what it had before; the game's current storm and distance from before the first Summon.
        private WanderingStorm summoned;
        private Vector3 summonedLocalPosition;
        private bool summonedActive;
        private ParticleSystem summonedCloud;
        private bool summonedEmitting;
        private bool summonedFilled;
        private ParticleSystem[] summonedSystems;
        private bool[] summonedUnscaled;
        private WanderingStormLightning lightning;
        private object savedCooldown;
        private ParticleSystem[] boltSystems;
        private float strikeTime;
        private float boltHoldEnd;
        private float lastStormFollow;
        // Better Storm's eye on the summoned storm (its StormEye, null for a storm without one): its clearness field, the clear
        // band's outer and inner edge, the clearness shown now and when taken.
        private Component summonedEye;
        private FieldInfo eyeClearnessField;
        private float eyeClearOuter;
        private float eyeClearInner;
        private float eyeClearness;
        private float savedEyeClearness;

        // The flash of a shown strike: its light's intensity (the strike's own until moved) and a factor on the sky's brightness
        // (the ambient colours, the fog colour, the sky's exposure); the values before the factor and as written, so it never
        // compounds.
        private Setting flashLight;
        private float flashSky = 1f;
        private Light strikeLight;
        private bool skyScaled;
        private readonly Color[] skyBase = new Color[4];
        private readonly Color[] skyWritten = new Color[4];
        private Material scaledSky;
        private float exposureBase;
        private float exposureWritten;
        private bool gameStormSaved;
        private WanderingStorm savedCurrentStorm;
        private float savedStormDistance;
        private Vector3 summonOrigin;
        private Vector3 summonForward;

        // The blend set's own sky copies, swapped in while its sky is the region's clear sky (a blend would write into that).
        private WeatherSet skySet;
        private Material savedDaySky;
        private Material savedDawnSky;
        private Material ownDaySky;
        private Material ownDawnSky;

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

            CollectStorms();
            for (int i = 0; storms != null && i < storms.Length; i++)
            {
                if (storms[i] != null)
                {
                    TakeStormParticles(storms[i].GetComponentsInChildren<ParticleSystem>(true));
                }
            }

            ScanBetterStormParticles(true);
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

            PutStormBack();
            RestoreWeather();
            for (int i = 0; i < stormParticles.Count; i++)
            {
                if (stormParticles[i] != null)
                {
                    ParticleSystem.MainModule main = stormParticles[i].main;
                    main.useUnscaledTime = stormParticlesUnscaled[i];
                }
            }

            stormParticles.Clear();
            stormParticlesUnscaled.Clear();
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

            if (summoned != null)
            {
                FollowSummonedStorm();
            }

            if (Time.unscaledTime >= nextParticleScan)
            {
                ScanBetterStormParticles(false);
            }
        }

        private void TakeStormParticles(ParticleSystem[] systems)
        {
            for (int i = 0; i < systems.Length; i++)
            {
                TakeStormParticle(systems[i], systems[i] != null && systems[i].main.useUnscaledTime);
            }
        }

        private void TakeStormParticle(ParticleSystem system, bool had)
        {
            if (system == null || stormParticles.Contains(system))
            {
                return;
            }

            ParticleSystem.MainModule main = system.main;
            stormParticles.Add(system);
            stormParticlesUnscaled.Add(had);
            main.useUnscaledTime = true;
        }

        // Better Storm makes its rain and snow under the shifting world and its sand beside the game's outer rain when first needed,
        // as copies of the game's rain, so one made while open carries the real time photo mode gave that rain; it is given back the
        // rain's own setting. Looked for once a second.
        private void ScanBetterStormParticles(bool atOpen)
        {
            nextParticleScan = Time.unscaledTime + 1f;
            bool rainHad = savedParticleUnscaled.Length > 3 && savedParticleUnscaled[3];
            Transform world = Refs.shiftingWorld;
            ParticleSystem outerRain = weatherParticles.Length > 3 ? weatherParticles[3] : null;
            Transform rainParent = outerRain != null ? outerRain.transform.parent : null;
            TakeBetterStormParticle(world != null ? world.Find("Better Storm Rain") : null, atOpen, rainHad);
            TakeBetterStormParticle(world != null ? world.Find("Better Storm Gentle Snow Particles") : null, atOpen, rainHad);
            TakeBetterStormParticle(rainParent != null ? rainParent.Find("Better Storm Sand Particles") : null, atOpen, rainHad);
        }

        private void TakeBetterStormParticle(Transform found, bool atOpen, bool rainHad)
        {
            ParticleSystem system = found != null ? found.GetComponent<ParticleSystem>() : null;
            if (system != null)
            {
                TakeStormParticle(system, atOpen ? system.main.useUnscaledTime : rainHad);
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

            if (boltHoldEnd > 0f)
            {
                HoldBolts();
            }

            if (strikeLight != null)
            {
                ApplyFlash();
            }
            else if (skyScaled)
            {
                RestoreSky();
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
            if (preset == 0 && summoned != null)
            {
                ApplySummonedWeather();
            }
            else if (preset == 0)
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
            else if (summoned != null)
            {
                ApplySummonedWeather();
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

        // The game's storm list (Better Storm adds its storms to it), named for the panel; the current storm selected.
        private void CollectStorms()
        {
            WeatherStorms weatherStorms = WeatherStorms.instance;
            storms = weatherStorms != null ? StormsField?.GetValue(weatherStorms) as WanderingStorm[] : null;
            int count = storms != null ? storms.Length : 0;
            stormNames = new string[count];
            stormIndex = 0;
            WanderingStorm current = weatherStorms != null ? weatherStorms.GetCurrentStorm() : null;
            int gameStorms = 0;
            for (int i = 0; i < count; i++)
            {
                string name = storms[i] != null ? storms[i].name : string.Empty;
                stormNames[i] = name.StartsWith("Better Storm ") ? name.Substring("Better Storm ".Length) : "Game storm " + ++gameStorms;
                if (storms[i] == current)
                {
                    stormIndex = i;
                }
            }
        }

        // Summon: the chosen storm placed by the distance and direction from the camera's place and view now, and the weather
        // worked out from it. A lit weather preset goes off, so the weather follows the storm.
        private void SummonStorm()
        {
            WeatherStorms weatherStorms = WeatherStorms.instance;
            WanderingStorm storm = stormIndex < storms.Length ? storms[stormIndex] : null;
            Transform view = StormDistanceOrigin(weatherStorms);
            if (weatherStorms == null || storm == null || view == null)
            {
                return;
            }

            if (!gameStormSaved)
            {
                savedCurrentStorm = weatherStorms.GetCurrentStorm();
                savedStormDistance = WeatherStorms.currentStormDistance;
                gameStormSaved = true;
            }

            summonOrigin = view.position;
            Vector3 forward = view.forward;
            forward.y = 0f;
            summonForward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            if (summoned != storm)
            {
                ReleaseStorm();
                TakeStorm(storm);
            }

            stormSummoned = true;
            weatherPreset = 0;
            MoveStorm();
        }

        // The storm the panel names taken instead of the summoned one, at the same distance and direction.
        private void ChangeSummonedStorm()
        {
            WanderingStorm storm = storms[stormIndex];
            if (summoned == null || storm == null || storm == summoned)
            {
                return;
            }

            ReleaseStorm();
            TakeStorm(storm);
            MoveStorm();
        }

        // What the storm had is kept; its particle systems run on real time and its cloud emits while summoned.
        private void TakeStorm(WanderingStorm storm)
        {
            summoned = storm;
            summonedLocalPosition = storm.transform.localPosition;
            summonedActive = storm.active;
            summonedSystems = storm.GetComponentsInChildren<ParticleSystem>(true);
            summonedUnscaled = new bool[summonedSystems.Length];
            for (int i = 0; i < summonedSystems.Length; i++)
            {
                ParticleSystem.MainModule main = summonedSystems[i].main;
                summonedUnscaled[i] = main.useUnscaledTime;
                main.useUnscaledTime = true;
            }

            summonedCloud = BottomParticlesField?.GetValue(storm) as ParticleSystem;
            summonedEmitting = summonedCloud != null && summonedCloud.emission.enabled;
            if (summonedCloud != null)
            {
                ParticleSystem.EmissionModule emission = summonedCloud.emission;
                emission.enabled = true;
            }

            summonedFilled = false;
            TakeEye(storm);
            lastStormFollow = Time.unscaledTime;
            lightning = StrikingLightning(storm);
            boltSystems = lightning != null ? lightning.GetComponentsInChildren<ParticleSystem>(true) : new ParticleSystem[0];
            boltHoldEnd = 0f;

            // A storm made active with its strike already due would strike at once and, game time standing still, keep that
            // flash lit with no bolt: its next strike is put off while summoned.
            savedCooldown = lightning != null ? CooldownField?.GetValue(lightning) : null;
            if (savedCooldown != null)
            {
                CooldownField.SetValue(lightning, float.MaxValue);
            }
        }

        // A storm has two lightning objects. The game strikes from "lightning (good)", switched on: a fade curve of three
        // flickers, a per-pixel light, its bolt. "lightning" is switched off and unused: no curve keys, a vertex light, a 600 m
        // bolt billboard. A storm with no switched-on lightning (Better Storm turns it off in storms without lightning) has none.
        private static WanderingStormLightning StrikingLightning(WanderingStorm storm)
        {
            WanderingStormLightning[] all = storm.GetComponentsInChildren<WanderingStormLightning>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].enabled && all[i].gameObject.activeInHierarchy)
                {
                    return all[i];
                }
            }

            return null;
        }

        // The lightning objects' particle systems (the game names them "lightning", "lightning (good)").
        private static bool IsBolt(ParticleSystem system)
        {
            return system.name.StartsWith("lightning");
        }

        private void MoveStorm()
        {
            Vector3 position = summonOrigin + Quaternion.Euler(0f, stormDirection, 0f) * summonForward * (stormDistance * 1000f);
            position.y = summoned.transform.position.y;
            summoned.transform.position = position;
            summoned.active = true;
            ApplySummonedWeather();

            // An empty cloud (the storm was beyond its cloud distance) is filled at once: on real time it fills over a minute.
            if (summonedCloud != null && summonedCloud.particleCount == 0)
            {
                for (int i = 0; i < summonedSystems.Length; i++)
                {
                    ParticleSystem system = summonedSystems[i];
                    if (system != null && !IsBolt(system) && system.particleCount == 0 && system.emission.enabled)
                    {
                        system.Simulate(60f, false, true);
                        system.Play(false);
                    }
                }

                summonedFilled = true;
            }
        }

        // The summoned storm made the game's current storm at the camera's distance, and the game's own blend of the region's
        // weather for that distance shown at once (Weather.ChangeWeather is blocked while open).
        private void ApplySummonedWeather()
        {
            WeatherStorms weatherStorms = WeatherStorms.instance;
            Transform view = StormDistanceOrigin(weatherStorms);
            if (weatherStorms == null || view == null)
            {
                return;
            }

            OwnBlendSky(weatherStorms);
            CurrentStormField.SetValue(weatherStorms, summoned);
            WeatherStorms.currentStormDistance = Vector3.Distance(view.position, summoned.transform.position);
            weatherStorms.ApplyStorm();
            if (weatherPreset == 0)
            {
                weather.targetWeatherSet = BlendedSetField.GetValue(weatherStorms) as WeatherSet;
                WeatherLerpField.SetValue(weather, 1f);
            }
            else
            {
                // ApplyStorm applied the storm's fog set; the lit preset's comes back in LateUpdate.
                weatherFogDirty = true;
            }
        }

        // Better Storm's eye clears by its clearness, which Better Storm eases with game time (still while paused); photo mode
        // eases it while the storm is summoned.
        private void TakeEye(WanderingStorm storm)
        {
            summonedEye = storm.GetComponent("StormEye");
            Type eyeType = summonedEye != null ? summonedEye.GetType() : null;
            eyeClearnessField = eyeType != null ? AccessTools.Field(eyeType, "shownClearness") : null;
            object shape = eyeType != null ? AccessTools.Property(eyeType, "Shape")?.GetValue(summonedEye, null) : null;
            Type shapeType = shape?.GetType();
            if (eyeClearnessField == null || shapeType == null
                || !(AccessTools.Field(shapeType, "HasEye")?.GetValue(shape) is bool hasEye) || !hasEye
                || !(AccessTools.Field(shapeType, "ClearOuter")?.GetValue(shape) is float outer)
                || !(AccessTools.Field(shapeType, "ClearInner")?.GetValue(shape) is float inner)
                || !(eyeClearnessField.GetValue(summonedEye) is float shown))
            {
                summonedEye = null;
                return;
            }

            eyeClearOuter = outer;
            eyeClearInner = inner;
            savedEyeClearness = shown;
            eyeClearness = Mathf.Max(0f, shown);
        }

        // The game's weather tick and Better Storm's eye both run on game time, which stands still while paused: every
        // StormFollowInterval of real time the summoned storm's weather is worked out again for where the camera is now, and
        // the eye eases toward the clearness of the camera's place as Better Storm does in play.
        private void FollowSummonedStorm()
        {
            float step = Time.unscaledTime - lastStormFollow;
            if (step < StormFollowInterval)
            {
                return;
            }

            lastStormFollow = Time.unscaledTime;
            Transform view = StormDistanceOrigin(WeatherStorms.instance);
            if (view == null)
            {
                return;
            }

            Vector3 offset = summoned.transform.position - view.position;
            bool changed = Mathf.Abs(offset.magnitude - WeatherStorms.currentStormDistance) > 1f;
            if (summonedEye != null)
            {
                offset.y = 0f;
                float target = Mathf.InverseLerp(eyeClearOuter, eyeClearInner, offset.magnitude);
                float shown = Mathf.MoveTowards(eyeClearness, target, Mathf.Min(step, Time.maximumDeltaTime) / EyeOpenSeconds);
                if (shown != eyeClearness)
                {
                    eyeClearness = shown;
                    eyeClearnessField.SetValue(summonedEye, shown);
                    changed = true;
                }
            }

            if (changed)
            {
                ApplySummonedWeather();
            }
        }

        // The point the game measures the storm distance from (the main camera).
        private Transform StormDistanceOrigin(WeatherStorms weatherStorms)
        {
            Transform origin = weatherStorms != null ? StormPlayerField?.GetValue(weatherStorms) as Transform : null;
            return origin != null ? origin : controller.CenterEye;
        }

        // In clear weather the game points the blend set's skies at the region's clear ones, and the next blend is written into
        // them; before each blend they are swapped for copies holding the same values.
        private void OwnBlendSky(WeatherStorms weatherStorms)
        {
            WeatherSet blended = BlendedSetField.GetValue(weatherStorms) as WeatherSet;
            WeatherSet clear = weather.currentRegion != null ? weather.currentRegion.clearWeather : null;
            if (blended == null || clear == null || blended.dayPalette.skyMaterial != clear.dayPalette.skyMaterial)
            {
                return;
            }

            if (ownDaySky == null)
            {
                skySet = blended;
                savedDaySky = blended.dayPalette.skyMaterial;
                savedDawnSky = blended.dawnPalette.skyMaterial;
                ownDaySky = new Material(savedDaySky);
                ownDawnSky = new Material(savedDawnSky);
            }
            else
            {
                ownDaySky.CopyPropertiesFromMaterial(blended.dayPalette.skyMaterial);
                ownDawnSky.CopyPropertiesFromMaterial(blended.dawnPalette.skyMaterial);
            }

            blended.dayPalette.skyMaterial = ownDaySky;
            blended.dawnPalette.skyMaterial = ownDawnSky;
        }

        // One strike from the summoned storm. The strike turns its light on only when it shows (within the light's range + 500 m
        // of the player); then its bolt and light run on real time to HoldAge and are held there.
        private void Strike()
        {
            if (summoned == null || lightning == null || LightningStrikeMethod == null)
            {
                return;
            }

            for (int i = 0; i < boltSystems.Length; i++)
            {
                boltSystems[i].Clear(false);
            }

            Light flash = lightning.GetComponent<Light>();
            if (flash != null)
            {
                flash.enabled = false;
            }

            strikeLight = null;
            LightningStrikeMethod.Invoke(lightning, null);
            if (flash == null || !flash.enabled)
            {
                return;
            }

            strikeLight = flash;
            for (int i = 0; i < boltSystems.Length; i++)
            {
                boltSystems[i].Play(false);
            }

            strikeTime = Time.unscaledTime;
            boltHoldEnd = strikeTime + HoldAge;
        }

        // The strike's age on real time given to its light timer, from which the game's Update sets the light by its curve (game
        // time stands still, so the timer stays where it is put); at HoldAge the bolt systems are paused.
        private void HoldBolts()
        {
            if (lightning != null)
            {
                LightTimerField?.SetValue(lightning, Mathf.Min(Time.unscaledTime - strikeTime, HoldAge));
            }

            if (Time.unscaledTime < boltHoldEnd)
            {
                return;
            }

            for (int i = 0; i < boltSystems.Length; i++)
            {
                if (boltSystems[i] != null && !boltSystems[i].isPaused)
                {
                    boltSystems[i].Pause(false);
                }
            }

            boltHoldEnd = 0f;
        }

        // After the game's writers of the frame: its Update sets the strike's light from its curve, and Weather.Update writes the
        // ambient colours, the fog colour and the sky's values each frame.
        private void ApplyFlash()
        {
            if (flashLight.Set)
            {
                strikeLight.intensity = flashLight.Value;
            }

            if (flashSky != 1f)
            {
                ScaleSky();
            }
            else if (skyScaled)
            {
                RestoreSky();
            }
        }

        private void ScaleSky()
        {
            RenderSettings.ambientLight = ScaledColour(0, RenderSettings.ambientLight);
            RenderSettings.ambientEquatorColor = ScaledColour(1, RenderSettings.ambientEquatorColor);
            RenderSettings.ambientGroundColor = ScaledColour(2, RenderSettings.ambientGroundColor);
            RenderSettings.fogColor = ScaledColour(3, RenderSettings.fogColor);
            Material sky = RenderSettings.skybox;
            if (sky != null && sky.HasProperty(ExposureId))
            {
                float exposure = sky.GetFloat(ExposureId);
                if (!skyScaled || sky != scaledSky || exposure != exposureWritten)
                {
                    exposureBase = exposure;
                }

                scaledSky = sky;
                exposureWritten = exposureBase * flashSky;
                sky.SetFloat(ExposureId, exposureWritten);
            }

            skyScaled = true;
        }

        // A value rewritten since the last frame is the new base; one still as written keeps its base.
        private Color ScaledColour(int index, Color current)
        {
            if (!skyScaled || current != skyWritten[index])
            {
                skyBase[index] = current;
            }

            skyWritten[index] = skyBase[index] * flashSky;
            return skyWritten[index];
        }

        // The values put back where nothing has rewritten them since.
        private void RestoreSky()
        {
            skyScaled = false;
            if (RenderSettings.ambientLight == skyWritten[0])
            {
                RenderSettings.ambientLight = skyBase[0];
            }

            if (RenderSettings.ambientEquatorColor == skyWritten[1])
            {
                RenderSettings.ambientEquatorColor = skyBase[1];
            }

            if (RenderSettings.ambientGroundColor == skyWritten[2])
            {
                RenderSettings.ambientGroundColor = skyBase[2];
            }

            if (RenderSettings.fogColor == skyWritten[3])
            {
                RenderSettings.fogColor = skyBase[3];
            }

            if (scaledSky != null && scaledSky.GetFloat(ExposureId) == exposureWritten)
            {
                scaledSky.SetFloat(ExposureId, exposureBase);
            }

            scaledSky = null;
        }

        // The summoned storm given back what it had: its place, its state, its particles' clock and emission, its lightning.
        private void ReleaseStorm()
        {
            if (summoned == null)
            {
                return;
            }

            summoned.transform.localPosition = summonedLocalPosition;
            summoned.active = summonedActive;
            for (int i = 0; i < summonedSystems.Length; i++)
            {
                ParticleSystem system = summonedSystems[i];
                if (system == null)
                {
                    continue;
                }

                ParticleSystem.MainModule main = system.main;
                main.useUnscaledTime = summonedUnscaled[i];
                if (IsBolt(system))
                {
                    // stopped rather than played on, so a held bolt doesn't emit again once the game runs
                    system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                else if (summonedFilled)
                {
                    system.Clear(false);
                }
            }

            if (summonedCloud != null)
            {
                ParticleSystem.EmissionModule emission = summonedCloud.emission;
                emission.enabled = summonedEmitting;
            }

            if (lightning != null)
            {
                Light flash = lightning.GetComponent<Light>();
                if (flash != null)
                {
                    flash.enabled = false;
                }

                if (savedCooldown != null)
                {
                    CooldownField.SetValue(lightning, savedCooldown);
                }
            }

            if (summonedEye != null)
            {
                eyeClearnessField.SetValue(summonedEye, savedEyeClearness);
                summonedEye = null;
            }

            summoned = null;
            lightning = null;
            boltHoldEnd = 0f;
            strikeLight = null;
            if (skyScaled)
            {
                RestoreSky();
            }
        }

        // Summon pressed again, Reset All and leaving: the storm, the game's current storm and distance and the blend set's skies
        // put back, the game's blend worked out again for them, then the weather as photo mode found it.
        private void PutStormBack()
        {
            stormSummoned = false;
            ReleaseStorm();
            WeatherStorms weatherStorms = WeatherStorms.instance;
            if (gameStormSaved && weatherStorms != null)
            {
                if (skySet != null)
                {
                    if (skySet.dayPalette.skyMaterial == ownDaySky)
                    {
                        skySet.dayPalette.skyMaterial = savedDaySky;
                    }

                    if (skySet.dawnPalette.skyMaterial == ownDawnSky)
                    {
                        skySet.dawnPalette.skyMaterial = savedDawnSky;
                    }
                }

                CurrentStormField.SetValue(weatherStorms, savedCurrentStorm);
                WeatherStorms.currentStormDistance = savedStormDistance;
                weatherStorms.ApplyStorm();
            }

            if (ownDaySky != null)
            {
                Destroy(ownDaySky);
                Destroy(ownDawnSky);
            }

            skySet = null;
            ownDaySky = ownDawnSky = savedDaySky = savedDawnSky = null;
            gameStormSaved = false;
            if (weatherPreset == 0)
            {
                RestoreWeather();
            }

            weatherFogDirty = true;
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

        // The layout, in the settings page's units; y runs down from the panel's top edge. A slider row: label, arrow, bar
        // strip, arrow, value, reset; a tick box sits where the row's first arrow does.
        private const float PanelWidth = 3.04f;
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
        private const float StepLeft = 1.38f;
        private const float StepWidth = 0.13f;
        private const float StepGap = 0.035f;
        private const float StripLeft = StepLeft + StepWidth + StepGap;
        private const float StripWidth = 0.60f;
        private const float MoreCentre = StripLeft + StripWidth + StepGap + StepWidth * 0.5f;
        private const float ValueRight = 2.69f;
        private const float ResetCentre = 2.815f;
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
        // An arrow held down steps again after the delay, then at the interval (real seconds).
        private const float StepRepeatDelay = 0.4f;
        private const float StepRepeatInterval = 0.06f;
        private const float MaximumStormDistance = 20f;
        private const float DefaultStormDistance = 8f;
        private const float MaximumFlashLight = 20f;
        private const float MaximumFlashSky = 4f;
        // The strike's age at which its bolt and light are held: the game's fade curve's third flicker at its peak (0.94 of the
        // strike's intensity), the bolt fully drawn.
        private const float HoldAge = 0.69f;
        // The game's interval between storm weather updates (WeatherStorms.Update).
        private const float StormFollowInterval = 0.05f;
        // Better Storm's eye goes fully clear or back in this long (StormEye.EyeOpenSeconds).
        private const float EyeOpenSeconds = 1.5f;

        // A panel value that follows the game's until it is moved; Reset makes it follow again.
        private struct Setting
        {
            public bool Set;
            public float Value;
        }

        private enum SliderId
        {
            Time, FieldOfView, FocusDistance, Aperture, Exposure, Contrast, Saturation, Temperature, Tint, BloomIntensity,
            BloomThreshold, Vignette, VignetteSoftness, FilmGrain, GrainSize, ChromaticAberration, Fog, VolumetricFog,
            StormDistance, StormDirection, FlashLight, FlashSky
        }

        private enum Part
        {
            HideUi, Leave, Weather, DepthOfField, HideBody, Grid, ResetAll, Slider, Reset, Less, More, StormPrevious, StormNext,
            Summon, Lightning
        }

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
            public Renderer LessPlate;
            public TextMesh LessText;
            public Renderer MorePlate;
            public TextMesh MoreText;
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
        private bool stormSummoned;
        private int stormIndex;
        private float stormDistance;
        private float stormDirection;

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
        private TextMesh stormNameText;
        private Renderer stormPreviousPlate;
        private Renderer stormNextPlate;
        private Renderer summonPlate;
        private Renderer lightningPlate;
        private TextMesh lightningText;
        private Material plateOn;
        private Material plateOff;
        private Color ink;
        private Color barColour;
        private Color plateInk;
        private readonly string[] barTexts = new string[BarCount + 1];
        private int hovered = -1;
        private SliderRow dragged;
        private int steppedHit = -1;
        private float nextStepTime;
        private int shownPreset;
        private int shownChecks;
        private int shownStorm;
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
            stormSummoned = false;
            stormDistance = DefaultStormDistance;
            stormDirection = 0f;
            flashLight = default(Setting);
            flashSky = 1f;
        }

        private void ResetAll()
        {
            if (summoned != null)
            {
                PutStormBack();
            }

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
            AddHeading(parts, "WEATHER", ref y);
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
            AddStormRows(parts, buttonWidth, rows, ref y);
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
            float x = StepLeft + BoxWidth * 0.5f;
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
            row.LessPlate = AddStep(parts, Part.Less, index, "<", "less " + label, StepLeft + StepWidth * 0.5f, y, out row.LessText);
            row.MorePlate = AddStep(parts, Part.More, index, ">", "more " + label, MoreCentre, y, out row.MoreText);
            if (HasReset(id))
            {
                row.ResetPlate = AddPlate(parts, parts.Plate, "reset " + label, ResetCentre, y, BoxWidth, BoxHeight);
                row.ResetArrow = AddText(parts, parts.Arrow, "arrow " + label, "↩", ResetCentre, y, TextAnchor.MiddleCenter, 0.8f);
                AddHit(Part.Reset, index, ResetCentre, y, BoxWidth, BoxHeight, row.ResetPlate);
            }

            y -= RowHeight * 0.5f;
            return row;
        }

        // A small plate with an arrow on it.
        private Renderer AddStep(PanelParts parts, Part part, int index, string arrow, string name, float x, float y, out TextMesh text)
        {
            Renderer plate = AddPlate(parts, parts.Plate, name, x, y, StepWidth, BoxHeight);
            plate.sharedMaterial = plateOn;
            text = AddText(parts, parts.PlateText, "text " + name, arrow, x, y, TextAnchor.MiddleCenter);
            AddHit(part, index, x, y, StepWidth, BoxHeight, plate);
            return plate;
        }

        // The storm's rows: the storm chosen with arrows on either side, Enable Storm and Summon Lightning, its distance and
        // direction, the strike's flash.
        private void AddStormRows(PanelParts parts, float buttonWidth, List<SliderRow> rows, ref float y)
        {
            y -= RowHeight * 0.5f;
            stormPreviousPlate = AddStep(parts, Part.StormPrevious, 0, "<", "storm previous", Padding + StepWidth * 0.5f, y, out _);
            stormNameText = AddText(parts, parts.Label, "storm name", string.Empty, PanelWidth * 0.5f, y, TextAnchor.MiddleCenter);
            stormNextPlate = AddStep(parts, Part.StormNext, 0, ">", "storm next", PanelWidth - Padding - StepWidth * 0.5f, y, out _);
            y -= RowHeight * 0.5f + ButtonRowHeight * 0.5f;
            summonPlate = AddButton(parts, Part.Summon, "Enable Storm", Padding + buttonWidth * 0.5f, y, buttonWidth);
            lightningPlate = AddButton(parts, Part.Lightning, "Summon Lightning", PanelWidth - Padding - buttonWidth * 0.5f, y, buttonWidth);
            lightningText = panelRoot.Find("text Summon Lightning").GetComponent<TextMesh>();
            y -= ButtonRowHeight * 0.5f;
            rows.Add(AddSlider(parts, SliderId.StormDistance, "storm distance", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.StormDirection, "storm direction", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.FlashLight, "flash light", rows.Count, ref y));
            rows.Add(AddSlider(parts, SliderId.FlashSky, "flash sky brightness", rows.Count, ref y));
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
            shownStorm = -1;
            RefreshPanel();
        }

        private void HidePanel()
        {
            SetHovered(-1);
            dragged = null;
            steppedHit = -1;
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
                if (hits[over].Part == Part.Less || hits[over].Part == Part.More)
                {
                    steppedHit = over;
                    nextStepTime = Time.unscaledTime + StepRepeatDelay;
                }
            }
            else if (steppedHit >= 0)
            {
                // An arrow held down keeps stepping while the pointer stays on it.
                if (!held || over != steppedHit)
                {
                    steppedHit = -1;
                }
                else if (Time.unscaledTime >= nextStepTime)
                {
                    nextStepTime = Time.unscaledTime + StepRepeatInterval;
                    Press(hits[over], point);
                }
            }
        }

        private bool CanPress(Hit hit)
        {
            switch (hit.Part)
            {
                case Part.HideBody:
                    return playerBody != null;
                case Part.Slider:
                case Part.Less:
                case Part.More:
                    return RowEnabled(sliderRows[hit.Index].Id);
                case Part.Reset:
                    return SettingOf(sliderRows[hit.Index].Id).Set;
                case Part.StormPrevious:
                case Part.StormNext:
                    return stormNames.Length > 1;
                case Part.Summon:
                    return stormNames.Length > 0;
                case Part.Lightning:
                    return stormSummoned && lightning != null;
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
                case Part.Less:
                    StepSlider(sliderRows[hit.Index].Id, -1);
                    break;
                case Part.More:
                    StepSlider(sliderRows[hit.Index].Id, 1);
                    break;
                case Part.StormPrevious:
                    stormIndex = (stormIndex + stormNames.Length - 1) % stormNames.Length;
                    ChangeSummonedStorm();
                    break;
                case Part.StormNext:
                    stormIndex = (stormIndex + 1) % stormNames.Length;
                    ChangeSummonedStorm();
                    break;
                case Part.Summon:
                    // The lit switch pressed again puts the storm back.
                    if (stormSummoned)
                    {
                        PutStormBack();
                    }
                    else
                    {
                        SummonStorm();
                    }

                    break;
                case Part.Lightning:
                    Strike();
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
                    row.LessPlate.sharedMaterial = row.MorePlate.sharedMaterial = enabledRow ? plateOn : plateOff;
                    row.LessText.color = row.MoreText.color = Faded(plateInk, enabledRow);
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

            int storm = stormNames.Length == 0 ? -2 : stormIndex * 2 + (stormSummoned ? 1 : 0);
            if (storm != shownStorm)
            {
                shownStorm = storm;
                bool any = stormNames.Length > 0;
                bool choose = stormNames.Length > 1;
                stormNameText.text = any ? "storm: " + stormNames[stormIndex] : "no storm";
                stormNameText.color = Faded(ink, any);
                stormPreviousPlate.sharedMaterial = stormNextPlate.sharedMaterial = choose ? plateOn : plateOff;
                summonPlate.sharedMaterial = stormSummoned ? plateOn : plateOff;
                bool strikes = stormSummoned && lightning != null;
                lightningPlate.sharedMaterial = strikes ? plateOn : plateOff;
                lightningText.color = Faded(plateInk, strikes);
            }
        }

        private static Color Faded(Color colour, bool full)
        {
            return full ? colour : new Color(colour.r, colour.g, colour.b, colour.a * DimAlpha);
        }

        private bool RowEnabled(SliderId id)
        {
            switch (id)
            {
                case SliderId.FocusDistance:
                case SliderId.Aperture:
                    return depthOfField;
                case SliderId.StormDistance:
                case SliderId.StormDirection:
                case SliderId.FlashLight:
                case SliderId.FlashSky:
                    return stormSummoned;
                default:
                    return true;
            }
        }

        // Plain values with no game value to follow have no reset.
        private static bool HasReset(SliderId id)
        {
            switch (id)
            {
                case SliderId.FocusDistance:
                case SliderId.Aperture:
                case SliderId.StormDistance:
                case SliderId.StormDirection:
                case SliderId.FlashSky:
                    return false;
                default:
                    return true;
            }
        }

        // The settings by slider, by reference; the plain values (focus distance, aperture, the storm's place, the flash sky
        // brightness) use time's slot only as a placeholder (their rows have no reset).
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
                case SliderId.FlashLight: return ref flashLight;
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

            if (id == SliderId.StormDistance)
            {
                return stormDistance;
            }

            if (id == SliderId.StormDirection)
            {
                return stormDirection;
            }

            if (id == SliderId.FlashSky)
            {
                return flashSky;
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
                case SliderId.FlashLight:
                    return strikeLight != null ? strikeLight.intensity : 0f;
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
                case SliderId.StormDistance: maximum = MaximumStormDistance; return;
                case SliderId.StormDirection: minimum = -180f; maximum = 180f; return;
                case SliderId.FlashLight: maximum = MaximumFlashLight; return;
                case SliderId.FlashSky: minimum = 1f; maximum = MaximumFlashSky; return;
                default: maximum = 1f; return;
            }
        }

        // One arrow press: the value shown, one step on, on the step's grid and within the range. Focus distance steps by a
        // hundredth of its logarithmic scale.
        private void StepSlider(SliderId id, int direction)
        {
            float value;
            if (id == SliderId.FocusDistance)
            {
                value = FromFraction(id, (Mathf.Round(Fraction(id, ShownValue(id)) * 100f) + direction) / 100f);
            }
            else
            {
                Range(id, out float minimum, out float maximum);
                float step = StepSize(id);
                value = Mathf.Clamp((Mathf.Round(ShownValue(id) / step) + direction) * step, minimum, maximum);
            }

            SetSlider(id, value);
        }

        // One step of each slider: the smallest change its shown value shows (5 minutes for the time of day).
        private static float StepSize(SliderId id)
        {
            switch (id)
            {
                case SliderId.Time: return 5f / 60f;
                case SliderId.FieldOfView:
                case SliderId.Temperature:
                case SliderId.Tint:
                case SliderId.StormDirection: return 1f;
                case SliderId.FlashLight: return 0.5f;
                case SliderId.Aperture:
                case SliderId.Exposure:
                case SliderId.StormDistance:
                case SliderId.FlashSky: return 0.1f;
                default: return 0.01f;
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

            if (id == SliderId.StormDistance || id == SliderId.StormDirection)
            {
                if (value == (id == SliderId.StormDistance ? stormDistance : stormDirection))
                {
                    return;
                }

                if (id == SliderId.StormDistance)
                {
                    stormDistance = value;
                }
                else
                {
                    stormDirection = value;
                }

                if (summoned != null)
                {
                    MoveStorm();
                }

                return;
            }

            // The flash values are applied each LateUpdate while a strike is shown.
            if (id == SliderId.FlashSky)
            {
                flashSky = value;
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
                case SliderId.StormDistance: return value.ToString("0.0") + " km";
                case SliderId.StormDirection: return value.ToString("+0;-0;0") + "°";
                case SliderId.FlashLight: return value.ToString("0.0");
                case SliderId.FlashSky: return "x" + value.ToString("0.0");
                default: return value.ToString("0.00");
            }
        }

        // To the nearest minute, so a time on the 5-minute step grid shows as that minute.
        private static string FormatTime(float hours)
        {
            int minutes = Mathf.RoundToInt(hours * 60f) % (24 * 60);
            if (minutes < 0)
            {
                minutes += 24 * 60;
            }

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
