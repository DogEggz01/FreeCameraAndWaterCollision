using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Crest;
using HarmonyLib;
using UnityEngine;

namespace FreeCameraAndCollision
{
    // Read by the Configuration Manager mod by field name, from a config entry's tags.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
        public bool? HideDefaultButton;
        public bool? ShowRangeAsPercent;
        public int? Order;
        public Action<ConfigEntryBase> CustomDrawer;
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class FreeCameraAndCollisionPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.DogEggz.sailwind.freecameraandcollision";
        public const string PluginName = "FreeCamera and Collision";
        public const string PluginVersion = "1.4.0";

        private static readonly GUIContent WorldMovementButton = new GUIContent("World", "Camera movement related to game world.");
        private static readonly GUIContent DroneMovementButton = new GUIContent("Drone", "Camera movement based on camera axis.");
        private static readonly GUIContent InvertPitchToggle = new GUIContent("Invert Mouse Pitch", "Invert vertical mouse input in Drone mode only.");

        // Settings of the removed solid collision, which an older .cfg may still hold.
        private static readonly ConfigDefinition[] ObsoleteConfigDefinitions =
        {
            new ConfigDefinition("Collision", "Collision Enabled"),
            new ConfigDefinition("Collision", "Solid Collision"),
            new ConfigDefinition("Collision Tuning", "Return Smooth Time")
        };

        // ConfigFile keeps the lines of a .cfg that no setting was bound to in this non-public property.
        private static readonly PropertyInfo OrphanedEntriesProperty = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");

        private Harmony harmony;

        internal static FreeCameraAndCollisionPlugin Instance { get; private set; }

        internal ConfigEntry<bool> WaterCollision { get; private set; }

        internal ConfigEntry<float> SphereRadius { get; private set; }

        internal ConfigEntry<float> SurfacePadding { get; private set; }

        internal ConfigEntry<FreeCameraMovementMode> MovementMode { get; private set; }

        internal ConfigEntry<int> CameraSpeed { get; private set; }

        internal ConfigEntry<int> CameraRollingSpeed { get; private set; }

        internal ConfigEntry<bool> InvertDroneMousePitch { get; private set; }

        internal ConfigEntry<bool> SwapBToP { get; private set; }

        private void Awake()
        {
            Instance = this;
            BindConfiguration();
            RemoveObsoleteConfiguration();
            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            if (BoatCamera.instance != null)
            {
                AttachController(BoatCamera.instance);
            }

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded.");
        }

        // Every plugin has run Awake by now, so Player Model's pause menu can be reached.
        private void Start()
        {
            PhotoMode.ConnectPlayerModel(this);
        }

        private void OnDestroy()
        {
            FreeCameraController.ExitActiveCamera();
            harmony?.UnpatchSelf();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        internal void AttachController(BoatCamera boatCamera)
        {
            if (boatCamera == null)
            {
                return;
            }

            FreeCameraController controller = boatCamera.GetComponent<FreeCameraController>() ?? boatCamera.gameObject.AddComponent<FreeCameraController>();
            controller.Initialize(boatCamera, this);
            (boatCamera.GetComponent<PhotoMode>() ?? boatCamera.gameObject.AddComponent<PhotoMode>()).Initialize(controller, this);
        }

        internal void LogInitializationError(string message)
        {
            Logger.LogError(message);
        }

        private void BindConfiguration()
        {
            SwapBToP = Config.Bind(
                "Free Camera",
                "Swap B to P",
                false,
                new ConfigDescription(
                    "Use P instead of B to enter or leave free camera. Changes apply immediately.",
                    null,
                    new ConfigurationManagerAttributes { Order = 40 }));
            MovementMode = Config.Bind(
                "Free Camera",
                "Movement Mode",
                FreeCameraMovementMode.World,
                new ConfigDescription(
                    "Select how free-camera movement is oriented.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = 10,
                        HideDefaultButton = true,
                        CustomDrawer = DrawMovementModeButtons
                    }));
            CameraSpeed = BindIntegerSlider(
                "Free Camera",
                "Camera Speed",
                10,
                1,
                40,
                "Free-camera positional movement speed. Shift doubles it and Alt halves it. Drone acceleration and stopping use a two-second response; Shift/Alt speed changes use one second.",
                20,
                true);
            CameraRollingSpeed = BindIntegerSlider(
                "Free Camera",
                "Camera Rolling Speed",
                60,
                10,
                120,
                "Q/E rolling speed in degrees per second. This is not affected by Camera Speed, Shift, or Alt.",
                30,
                false);
            // Hidden from Configuration Manager's list: the Movement Mode drawer shows it under Drone.
            InvertDroneMousePitch = Config.Bind(
                "Drone Mode",
                "Invert Mouse Pitch",
                false,
                new ConfigDescription(
                    "Invert vertical mouse input in Drone mode only.",
                    null,
                    new ConfigurationManagerAttributes { Browsable = false }));
            WaterCollision = Config.Bind(
                "Collision",
                "Water Collision",
                true,
                "Keep the camera sphere above the animated Crest water surface. Changes apply immediately.");
            SphereRadius = BindSlider(
                "Collision Tuning",
                "Sphere Radius",
                0.3f,
                0.05f,
                1.5f,
                "Radius used to sample and stay above the animated water surface.",
                30);
            SurfacePadding = BindSlider(
                "Collision Tuning",
                "Surface Padding",
                0.1f,
                0f,
                0.5f,
                "Additional height kept between the camera sphere and the water surface.",
                20);
        }

        // Deletes the obsolete settings' lines from an existing .cfg.
        private void RemoveObsoleteConfiguration()
        {
            try
            {
                IDictionary<ConfigDefinition, string> orphanedEntries = OrphanedEntriesProperty?.GetValue(Config, null) as IDictionary<ConfigDefinition, string>;
                if (orphanedEntries == null)
                {
                    Logger.LogWarning("Could not inspect obsolete configuration entries.");
                    return;
                }

                bool removed = false;
                for (int i = 0; i < ObsoleteConfigDefinitions.Length; i++)
                {
                    removed |= orphanedEntries.Remove(ObsoleteConfigDefinitions[i]);
                }

                if (removed)
                {
                    Config.Save();
                    Logger.LogInfo("Removed obsolete solid-collision configuration.");
                }
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Could not remove obsolete configuration entries: " + exception.GetBaseException().Message);
            }
        }

        private ConfigEntry<float> BindSlider(string section, string key, float defaultValue, float minimum, float maximum, string description, int order)
        {
            return Config.Bind(
                section,
                key,
                defaultValue,
                new ConfigDescription(
                    description,
                    new AcceptableValueRange<float>(minimum, maximum),
                    new ConfigurationManagerAttributes
                    {
                        ShowRangeAsPercent = false,
                        Order = order
                    }));
        }

        private ConfigEntry<int> BindIntegerSlider(string section, string key, int defaultValue, int minimum, int maximum, string description, int order, bool showDefaultButton)
        {
            return Config.Bind(
                section,
                key,
                defaultValue,
                new ConfigDescription(
                    description,
                    new AcceptableValueRange<int>(minimum, maximum),
                    new ConfigurationManagerAttributes
                    {
                        ShowRangeAsPercent = false,
                        HideDefaultButton = !showDefaultButton,
                        Order = order
                    }));
        }

        // Configuration Manager's row for Movement Mode: a World and a Drone button, and under Drone the pitch toggle.
        private static void DrawMovementModeButtons(ConfigEntryBase setting)
        {
            ConfigEntry<FreeCameraMovementMode> movementMode = setting as ConfigEntry<FreeCameraMovementMode>;
            if (movementMode == null)
            {
                return;
            }

            bool wasEnabled = GUI.enabled;
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUI.enabled = wasEnabled && movementMode.Value > FreeCameraMovementMode.World;
            if (GUILayout.Button(WorldMovementButton, GUILayout.ExpandWidth(true)))
            {
                movementMode.Value = FreeCameraMovementMode.World;
            }

            GUI.enabled = wasEnabled && movementMode.Value != FreeCameraMovementMode.Drone;
            if (GUILayout.Button(DroneMovementButton, GUILayout.ExpandWidth(true)))
            {
                movementMode.Value = FreeCameraMovementMode.Drone;
            }

            GUILayout.EndHorizontal();
            GUI.enabled = wasEnabled;
            if (movementMode.Value == FreeCameraMovementMode.Drone && Instance?.InvertDroneMousePitch != null)
            {
                bool invert = Instance.InvertDroneMousePitch.Value;
                bool newInvert = GUILayout.Toggle(invert, InvertPitchToggle, GUILayout.ExpandWidth(true));
                if (newInvert != invert)
                {
                    Instance.InvertDroneMousePitch.Value = newInvert;
                }
            }

            GUILayout.EndVertical();
        }
    }

    // Sits on the BoatCamera object. Runs after the game's scripts each frame, so its camera pose is the last one set.
    [DefaultExecutionOrder(10000)]
    internal sealed class FreeCameraController : MonoBehaviour
    {
        private const KeyCode DefaultToggleKey = KeyCode.B;
        private const KeyCode AlternativeToggleKey = KeyCode.P;
        private const KeyCode FollowShipToggleKey = KeyCode.H;
        private const KeyCode ControllerToggleKey = KeyCode.JoystickButton8;
        private const KeyCode ControllerDescendKey = KeyCode.JoystickButton4;
        private const KeyCode ControllerRiseKey = KeyCode.JoystickButton5;
        private const float SpeedBoostMultiplier = 2f;
        private const float PrecisionSpeedMultiplier = 0.5f;
        private const float MinimumZoom = 20f;
        private const float MouseZoomSpeed = 30f;
        private const float ControllerZoomSpeed = 35f;
        private const float ZoomResponseSpeed = 100f;
        private const float WorldShiftDistanceThreshold = 20f;
        private const string PrimaryHorizontalAxis = "Oculus_CrossPlatform_PrimaryThumbstickHorizontal";
        private const string PrimaryVerticalAxis = "Oculus_CrossPlatform_PrimaryThumbstickVertical";
        private const string SecondaryHorizontalAxis = "Oculus_CrossPlatform_SecondaryThumbstickHorizontal";
        private const string SecondaryVerticalAxis = "Oculus_CrossPlatform_SecondaryThumbstickVertical";
        private const string PrimaryTriggerAxis = "Oculus_CrossPlatform_PrimaryIndexTrigger";
        private const string SecondaryTriggerAxis = "Oculus_CrossPlatform_SecondaryIndexTrigger";
        private const string MouseHorizontalAxis = "Mouse X";
        private const string MouseVerticalAxis = "Mouse Y";
        private const string MouseScrollAxis = "Mouse ScrollWheel";

        private static readonly FieldInfo CenterEyeField = AccessTools.Field(typeof(BoatCamera), "centerEye");
        private static readonly FieldInfo PlayerLooksField = AccessTools.Field(typeof(BoatCamera), "playerLooks");
        private static readonly FieldInfo BoatLooksField = AccessTools.Field(typeof(BoatCamera), "boatLooks");

        private BoatCamera boatCamera;
        private FreeCameraAndCollisionPlugin plugin;
        private CameraConstraintResolver constraintResolver;
        private Transform centerEye;
        private Camera unityCamera;
        private MouseLook[] playerLooks;
        private MouseLook[] boatLooks;
        private bool[] savedBoatLookStates;
        private readonly FreeCameraRotationState rotationState = new FreeCameraRotationState();
        private readonly DroneVelocityState droneVelocityState = new DroneVelocityState();
        private bool initialized;
        private bool freeCameraActive;
        private Transform followedBoat;
        private Transform anchor;
        private bool followShipTranslation;
        private Vector3 lastAnchorPosition;
        private Vector3 freeCameraPosition;
        private float lookSensitivityX = 2f;
        private float lookSensitivityY = 2f;
        private float minimumLookY = -80f;
        private float maximumLookY = 80f;
        private Vector3 savedCenterEyeLocalPosition;
        private Quaternion savedCenterEyeLocalRotation;
        private float savedFieldOfView;
        private float targetFieldOfView;
        private OVRPlayerController playerController;
        private bool savedPlayerControllerEnabled;

        internal static FreeCameraController ActiveController { get; private set; }

        internal static bool IsFreeCameraOn
        {
            get
            {
                return ActiveController != null && ActiveController.freeCameraActive;
            }
        }

        // The game's input answers "nothing pressed" while free camera or photo mode is on.
        internal static bool IsInputCaptured
        {
            get
            {
                return PhotoMode.IsOpen || (ActiveController != null && ActiveController.freeCameraActive);
            }
        }

        internal Transform CenterEye
        {
            get
            {
                return centerEye;
            }
        }

        internal Camera UnityCamera
        {
            get
            {
                return unityCamera;
            }
        }

        // The widest zoom: the field of view when free camera started.
        internal float MaximumZoom
        {
            get
            {
                return savedFieldOfView;
            }
        }

        internal void SetZoom(float fieldOfView)
        {
            targetFieldOfView = Mathf.Clamp(fieldOfView, MinimumZoom, savedFieldOfView);
        }

        internal void Initialize(BoatCamera owner, FreeCameraAndCollisionPlugin ownerPlugin)
        {
            if (initialized && boatCamera == owner && plugin == ownerPlugin)
            {
                return;
            }

            boatCamera = owner;
            plugin = ownerPlugin;
            centerEye = CenterEyeField?.GetValue(owner) as Transform;
            playerLooks = PlayerLooksField?.GetValue(owner) as MouseLook[];
            boatLooks = BoatLooksField?.GetValue(owner) as MouseLook[];
            if (centerEye == null)
            {
                ownerPlugin.LogInitializationError("Free camera could not initialize: BoatCamera.centerEye was not found.");
                enabled = false;
                return;
            }

            unityCamera = centerEye.GetComponent<Camera>();
            if (unityCamera == null)
            {
                unityCamera = centerEye.GetComponentInChildren<Camera>();
            }

            if (unityCamera == null)
            {
                unityCamera = Camera.main;
            }

            constraintResolver = new CameraConstraintResolver(ownerPlugin, this);
            CacheVanillaFirstPersonLookSettings();
            initialized = true;
        }

        internal static void ExitActiveCamera()
        {
            ActiveController?.ExitFreeCamera();
        }

        internal static void ExitIfOwnedBy(BoatCamera owner)
        {
            if (ActiveController != null && ActiveController.boatCamera == owner)
            {
                ActiveController.ExitFreeCamera();
            }
        }

        private void Update()
        {
            if (!initialized)
            {
                return;
            }

            bool togglePressed = Input.GetKeyDown(plugin.SwapBToP.Value ? AlternativeToggleKey : DefaultToggleKey)
                || (GameInput.controllerEnabled && Input.GetKeyDown(ControllerToggleKey));
            if (!freeCameraActive)
            {
                if (togglePressed && CanEnterFreeCamera())
                {
                    EnterFreeCamera();
                    return;
                }
            }
            else
            {
                if (togglePressed || !CanRemainActive())
                {
                    ExitFreeCamera();
                    return;
                }

                UpdateFreeCameraInput();
            }
        }

        private void LateUpdate()
        {
            if (!initialized)
            {
                return;
            }

            if (freeCameraActive)
            {
                // Every frame: the game's BoatCamera.Update puts the camera back at its own spot.
                centerEye.SetPositionAndRotation(freeCameraPosition, rotationState.Rotation);
                return;
            }

            if (BoatCamera.on && GameState.currentBoat != null)
            {
                ApplyThirdPersonWaterConstraint();
            }
        }

        // Puts the pose back when a camera placed just before rendering (Player Model's following camera) moved it after
        // LateUpdate. Registered on entering, so it runs after such placements registered earlier.
        private void HoldPoseBeforeRender()
        {
            if (centerEye.position != freeCameraPosition || centerEye.rotation != rotationState.Rotation)
            {
                centerEye.SetPositionAndRotation(freeCameraPosition, rotationState.Rotation);
            }
        }

        private void OnDisable()
        {
            if (freeCameraActive)
            {
                ExitFreeCamera();
            }
        }

        private void OnDestroy()
        {
            if (freeCameraActive)
            {
                ExitFreeCamera();
            }
        }

        // In photo mode free camera starts from any view, first person included.
        private bool CanEnterFreeCamera()
        {
            return (PhotoMode.IsOpen || IsFreeCameraContextAvailable()) && CurrentAnchor() != null;
        }

        private bool CanRemainActive()
        {
            return (PhotoMode.IsOpen || IsFreeCameraContextAvailable()) && anchor != null && GameState.currentBoat == followedBoat;
        }

        // The game's third-person camera is on, in a running game, outside a shipyard, a cursor menu and VR. Ashore it is on
        // only through a third-person view of the player, such as Player Model's following camera: the game's own boat
        // camera switches off without a boat before this script's Update runs.
        private static bool IsFreeCameraContextAvailable()
        {
            return BoatCamera.on
                && GameState.playing
                && !GameState.currentlyLoading
                && GameState.currentShipyard == null
                && !GameState.inCursorMenu
                && !GameState.VREnabled;
        }

        // What the free camera follows and stays within reach of: the boat the player is on, or else the player.
        private static Transform CurrentAnchor()
        {
            if (GameState.currentBoat != null)
            {
                return GameState.currentBoat;
            }

            return Refs.observerMirror != null ? Refs.observerMirror.transform : null;
        }

        private void EnterFreeCamera()
        {
            if (ActiveController != null && ActiveController != this)
            {
                ActiveController.ExitFreeCamera();
            }

            followedBoat = GameState.currentBoat;
            anchor = CurrentAnchor();
            followShipTranslation = true;
            lastAnchorPosition = anchor.position;
            freeCameraPosition = centerEye.position;
            rotationState.Initialize(centerEye.rotation, plugin.MovementMode.Value, minimumLookY, maximumLookY);
            droneVelocityState.Reset(GetMovementSpeedMultiplier());
            savedCenterEyeLocalPosition = centerEye.localPosition;
            savedCenterEyeLocalRotation = centerEye.localRotation;
            if (unityCamera != null)
            {
                savedFieldOfView = unityCamera.fieldOfView;
                targetFieldOfView = savedFieldOfView;
            }

            SaveAndDisableBoatLook();
            SaveAndDisablePlayerMovement();
            ActiveController = this;
            freeCameraActive = true;
            Application.onBeforeRender -= HoldPoseBeforeRender;
            Application.onBeforeRender += HoldPoseBeforeRender;
        }

        private void ExitFreeCamera()
        {
            if (!freeCameraActive)
            {
                return;
            }

            freeCameraActive = false;
            Application.onBeforeRender -= HoldPoseBeforeRender;
            if (centerEye != null)
            {
                centerEye.localPosition = savedCenterEyeLocalPosition;
                centerEye.localRotation = savedCenterEyeLocalRotation;
            }

            if (unityCamera != null)
            {
                unityCamera.fieldOfView = savedFieldOfView;
            }

            RestoreBoatLook();
            RestorePlayerMovement();
            followedBoat = null;
            anchor = null;
            droneVelocityState.Reset(1f);
            if (ActiveController == this)
            {
                ActiveController = null;
            }
        }

        private void UpdateFreeCameraInput()
        {
            // Real time, so the camera moves while the game is paused or sped up; capped so a long frame is no jump.
            float deltaTime = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (Input.GetKeyDown(FollowShipToggleKey))
            {
                followShipTranslation = !followShipTranslation;
            }

            if (rotationState.SetMode(plugin.MovementMode.Value))
            {
                droneVelocityState.Reset(GetMovementSpeedMultiplier());
            }

            UpdateFreeCameraLook(deltaTime);
            UpdateFreeCameraMovement(deltaTime);
            UpdateFreeCameraZoom(deltaTime);
        }

        // Same sensitivity formula as the game's MouseLook.
        private void UpdateFreeCameraLook(float deltaTime)
        {
            float controllerYaw = 0f;
            float controllerPitch = 0f;
            if (GameInput.controllerEnabled)
            {
                controllerYaw = Input.GetAxis(SecondaryHorizontalAxis);
                controllerPitch = Input.GetAxis(SecondaryVerticalAxis);
            }

            float mouseX = 0f;
            float mouseY = 0f;
            // With photo mode's panel shown the cursor is for the panel: the mouse looks only while the right button is held.
            if (!PhotoMode.IsPanelShown || Input.GetMouseButton(1))
            {
                mouseX = Input.GetAxis(MouseHorizontalAxis);
                mouseY = Input.GetAxis(MouseVerticalAxis);
            }

            if (controllerYaw != 0f || controllerPitch != 0f)
            {
                GameState.lookingWithController = true;
            }

            if (mouseX != 0f || mouseY != 0f)
            {
                GameState.lookingWithController = false;
            }

            float sensitivity = Settings.mouseSens / 5f + 0.1f;
            float yawDelta = (controllerYaw + mouseX) * lookSensitivityX * sensitivity;
            float rollInput = GetKeyboardAxis(KeyCode.Q, KeyCode.E);
            rotationState.UpdateLook(
                yawDelta,
                mouseY * lookSensitivityY * sensitivity,
                controllerPitch * lookSensitivityY * sensitivity,
                plugin.InvertDroneMousePitch.Value,
                rollInput,
                plugin.CameraRollingSpeed.Value,
                Input.GetKeyDown(KeyCode.R),
                deltaTime);
        }

        private void UpdateFreeCameraMovement(float deltaTime)
        {
            Vector3 anchorPosition = anchor.position;
            Vector3 anchorDelta = anchorPosition - lastAnchorPosition;
            lastAnchorPosition = anchorPosition;
            if (anchorDelta.sqrMagnitude > WorldShiftDistanceThreshold * WorldShiftDistanceThreshold)
            {
                // A jump this large in one frame is the game shifting the world: the camera shifts with it.
                freeCameraPosition += anchorDelta;
                anchorDelta = Vector3.zero;
            }
            else if (!followShipTranslation)
            {
                anchorDelta = Vector3.zero;
            }

            float forwardInput = GetKeyboardAxis(KeyCode.W, KeyCode.S);
            float rightInput = GetKeyboardAxis(KeyCode.D, KeyCode.A);
            float upInput = GetKeyboardAxis(KeyCode.Space, KeyCode.LeftControl);
            if (Input.GetKey(KeyCode.RightControl))
            {
                upInput -= 1f;
            }

            if (GameInput.controllerEnabled)
            {
                forwardInput += Input.GetAxis(PrimaryVerticalAxis);
                rightInput += Input.GetAxis(PrimaryHorizontalAxis);
                if (Input.GetKey(ControllerRiseKey))
                {
                    upInput += 1f;
                }

                if (Input.GetKey(ControllerDescendKey))
                {
                    upInput -= 1f;
                }
            }

            float speedMultiplier = GetMovementSpeedMultiplier();
            Vector3 movement;
            if (rotationState.Mode == FreeCameraMovementMode.Drone)
            {
                Vector3 localInput = new Vector3(rightInput, upInput, forwardInput);
                Vector3 localVelocity = droneVelocityState.Update(localInput, plugin.CameraSpeed.Value, speedMultiplier, deltaTime);
                movement = rotationState.Rotation * localVelocity * deltaTime;
            }
            else
            {
                Quaternion movementRotation = rotationState.MovementRotation;
                Vector3 direction = movementRotation * Vector3.forward * forwardInput + movementRotation * Vector3.right * rightInput + Vector3.up * upInput;
                if (direction.sqrMagnitude > 1f)
                {
                    direction.Normalize();
                }

                movement = direction * plugin.CameraSpeed.Value * speedMultiplier * deltaTime;
            }

            Vector3 focus = anchorPosition;
            Vector3 requestedPosition = freeCameraPosition + anchorDelta + movement;
            Vector3 constrainedPosition = constraintResolver.ConstrainFreeCameraPosition(requestedPosition, focus, CameraDistanceLimits.FreeCameraMaximumDistanceFromBoat);
            if (rotationState.Mode == FreeCameraMovementMode.Drone)
            {
                droneVelocityState.RemoveBlockedComponent(rotationState.Rotation, constrainedPosition - requestedPosition);
            }

            freeCameraPosition = constrainedPosition;
        }

        private static float GetMovementSpeedMultiplier()
        {
            float multiplier = 1f;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                multiplier *= SpeedBoostMultiplier;
            }

            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            {
                multiplier *= PrecisionSpeedMultiplier;
            }

            return multiplier;
        }

        // Zooms by narrowing the field of view, never wider than it was on entering.
        private void UpdateFreeCameraZoom(float deltaTime)
        {
            if (unityCamera == null)
            {
                return;
            }

            float mouseScroll = PhotoMode.IsMouseOverPanel ? 0f : Input.GetAxis(MouseScrollAxis);
            float triggerZoom = 0f;
            if (GameInput.controllerEnabled)
            {
                triggerZoom = Input.GetAxis(SecondaryTriggerAxis) - Input.GetAxis(PrimaryTriggerAxis);
            }

            targetFieldOfView -= mouseScroll * MouseZoomSpeed;
            targetFieldOfView -= triggerZoom * ControllerZoomSpeed * deltaTime;
            targetFieldOfView = Mathf.Clamp(targetFieldOfView, MinimumZoom, savedFieldOfView);
            float fieldOfView = unityCamera.fieldOfView;
            if (fieldOfView != targetFieldOfView)
            {
                unityCamera.fieldOfView = Mathf.MoveTowards(fieldOfView, targetFieldOfView, ZoomResponseSpeed * deltaTime);
            }
        }

        private void ApplyThirdPersonWaterConstraint()
        {
            Vector3 position = centerEye.position;
            Vector3 constrained = constraintResolver.ApplyWaterCollision(position);
            if (constrained.y != position.y)
            {
                centerEye.position = constrained;
            }
        }

        // Takes the look sensitivity and pitch limits from the game's first-person MouseLooks.
        private void CacheVanillaFirstPersonLookSettings()
        {
            if (playerLooks == null)
            {
                return;
            }

            for (int i = 0; i < playerLooks.Length; i++)
            {
                MouseLook look = playerLooks[i];
                if (look == null)
                {
                    continue;
                }

                if (look.axes == MouseLook.RotationAxes.MouseX || look.axes == MouseLook.RotationAxes.MouseXAndY)
                {
                    lookSensitivityX = look.sensitivityX;
                }

                if (look.axes == MouseLook.RotationAxes.MouseY || look.axes == MouseLook.RotationAxes.MouseXAndY)
                {
                    lookSensitivityY = look.sensitivityY;
                    minimumLookY = look.minimumY;
                    maximumLookY = look.maximumY;
                }
            }
        }

        private void SaveAndDisableBoatLook()
        {
            if (boatLooks == null)
            {
                savedBoatLookStates = null;
                return;
            }

            savedBoatLookStates = new bool[boatLooks.Length];
            for (int i = 0; i < boatLooks.Length; i++)
            {
                MouseLook look = boatLooks[i];
                if (look == null)
                {
                    continue;
                }

                savedBoatLookStates[i] = look.enabled;
                look.enabled = false;
            }
        }

        private void RestoreBoatLook()
        {
            if (boatLooks == null || savedBoatLookStates == null)
            {
                return;
            }

            int count = Mathf.Min(boatLooks.Length, savedBoatLookStates.Length);
            for (int i = 0; i < count; i++)
            {
                if (boatLooks[i] != null)
                {
                    boatLooks[i].enabled = savedBoatLookStates[i];
                }
            }

            savedBoatLookStates = null;
        }

        private void SaveAndDisablePlayerMovement()
        {
            playerController = Refs.ovrController;
            if (playerController == null)
            {
                return;
            }

            savedPlayerControllerEnabled = playerController.enabled;
            playerController.Stop();
            playerController.enabled = false;
        }

        private void RestorePlayerMovement()
        {
            if (playerController != null)
            {
                playerController.enabled = savedPlayerControllerEnabled;
            }

            playerController = null;
        }

        private static float GetKeyboardAxis(KeyCode positive, KeyCode negative)
        {
            float axis = 0f;
            if (Input.GetKey(positive))
            {
                axis += 1f;
            }

            if (Input.GetKey(negative))
            {
                axis -= 1f;
            }

            return axis;
        }
    }

    internal enum FreeCameraMovementMode
    {
        World,
        Drone
    }

    internal static class CameraDistanceLimits
    {
        internal const float FreeCameraMaximumDistanceFromBoat = 320f;
    }

    // The free camera's rotation: yaw, pitch and roll angles in World mode, a free quaternion in Drone mode.
    internal sealed class FreeCameraRotationState
    {
        private const float RollReferenceEpsilonSquared = 0.0001f;
        private const float RollResetSpeedDegreesPerSecond = 120f;

        private float minimumPitch;
        private float maximumPitch;
        private float worldYaw;
        private float worldPitch;
        private float worldRoll;
        private bool rollResetActive;

        internal Quaternion Rotation { get; private set; }

        internal FreeCameraMovementMode Mode { get; private set; }

        // World mode moves along the view without its roll.
        internal Quaternion MovementRotation
        {
            get
            {
                return Mode == FreeCameraMovementMode.Drone ? Rotation : Quaternion.Euler(-worldPitch, worldYaw, 0f);
            }
        }

        internal void Initialize(Quaternion initialRotation, FreeCameraMovementMode mode, float minimumWorldPitch, float maximumWorldPitch)
        {
            Rotation = initialRotation;
            Mode = mode;
            minimumPitch = minimumWorldPitch;
            maximumPitch = maximumWorldPitch;
            rollResetActive = false;
            UpdateWorldAnglesFromRotation();
        }

        // Returns true when the mode changed.
        internal bool SetMode(FreeCameraMovementMode mode)
        {
            if (mode == Mode)
            {
                return false;
            }

            Mode = mode;
            if (Mode == FreeCameraMovementMode.World)
            {
                UpdateWorldAnglesFromRotation();
            }

            return true;
        }

        internal void UpdateLook(float yawDelta, float mousePitchDelta, float controllerPitchDelta, bool invertDroneMousePitch, float rollInput, float rollingSpeed, bool resetRollPressed, float deltaTime)
        {
            float mousePitchSign = Mode == FreeCameraMovementMode.Drone && invertDroneMousePitch ? -1f : 1f;
            float pitchDelta = controllerPitchDelta + mousePitchDelta * mousePitchSign;
            float rollDelta = rollInput * rollingSpeed * deltaTime;
            if (rollInput != 0f)
            {
                rollResetActive = false;
            }
            else if (resetRollPressed)
            {
                rollResetActive = true;
            }

            if (Mode == FreeCameraMovementMode.Drone)
            {
                UpdateDroneRotation(yawDelta, pitchDelta, rollDelta, deltaTime);
                return;
            }

            UpdateWorldRotation(yawDelta, pitchDelta, rollDelta, deltaTime);
        }

        private void UpdateWorldRotation(float yawDelta, float pitchDelta, float rollDelta, float deltaTime)
        {
            worldYaw = NormalizeAngle(worldYaw + yawDelta);
            worldPitch = Mathf.Clamp(worldPitch + pitchDelta, minimumPitch, maximumPitch);
            if (rollResetActive)
            {
                worldRoll = Mathf.MoveTowardsAngle(worldRoll, 0f, RollResetSpeedDegreesPerSecond * deltaTime);
                if (worldRoll == 0f)
                {
                    rollResetActive = false;
                }
            }
            else
            {
                worldRoll = NormalizeAngle(worldRoll + rollDelta);
            }

            Rotation = Quaternion.Euler(-worldPitch, worldYaw, worldRoll);
        }

        private void UpdateDroneRotation(float yawDelta, float pitchDelta, float rollDelta, float deltaTime)
        {
            Quaternion delta = Quaternion.Euler(-pitchDelta, yawDelta, rollDelta);
            Rotation = (Rotation * delta).normalized;
            if (rollResetActive)
            {
                UpdateDroneRollReset(deltaTime);
            }
        }

        private void UpdateWorldAnglesFromRotation()
        {
            Vector3 eulerAngles = Rotation.eulerAngles;
            worldYaw = NormalizeAngle(eulerAngles.y);
            worldPitch = Mathf.Clamp(-NormalizeAngle(eulerAngles.x), minimumPitch, maximumPitch);
            worldRoll = NormalizeAngle(eulerAngles.z);
        }

        // Turns the camera about its view axis until its up lies in the plane of the view and world up.
        private void UpdateDroneRollReset(float deltaTime)
        {
            Vector3 forward = Rotation * Vector3.forward;
            Vector3 up = Rotation * Vector3.up;
            Vector3 levelUp = Vector3.ProjectOnPlane(Vector3.up, forward);
            if (levelUp.sqrMagnitude < RollReferenceEpsilonSquared)
            {
                // Looking straight up or down: world up gives no reference.
                levelUp = Vector3.ProjectOnPlane(Vector3.forward, forward);
            }

            if (levelUp.sqrMagnitude < RollReferenceEpsilonSquared)
            {
                return;
            }

            levelUp.Normalize();
            float roll = Vector3.SignedAngle(levelUp, up, forward);
            float newRoll = Mathf.MoveTowardsAngle(roll, 0f, RollResetSpeedDegreesPerSecond * deltaTime);
            float rollStep = Mathf.DeltaAngle(roll, newRoll);
            Rotation = (Rotation * Quaternion.AngleAxis(rollStep, Vector3.forward)).normalized;
            if (newRoll == 0f)
            {
                rollResetActive = false;
            }
        }

        private static float NormalizeAngle(float angle)
        {
            while (angle > 180f)
            {
                angle -= 360f;
            }

            while (angle < -180f)
            {
                angle += 360f;
            }

            return angle;
        }
    }

    // Drone mode's velocity in the camera's own axes: it speeds up, slows down and brakes over time.
    internal sealed class DroneVelocityState
    {
        private const float VelocityTransitionTime = 2f;
        private const float SpeedMultiplierTransitionTime = 1f;
        private const float OppositeInputDecelerationTime = 1f;
        private const float MovementInputEpsilonSquared = 0.0001f;
        private const float CollisionCorrectionEpsilonSquared = 1E-06f;

        private float currentSpeedMultiplier = 1f;
        private float speedMultiplierTransitionStart = 1f;
        private float speedMultiplierTransitionTarget = 1f;
        private float speedMultiplierTransitionElapsed;
        private bool movementInputWasActive;
        private float stoppingVelocityChangeRate;

        internal Vector3 LocalVelocity { get; private set; }

        internal void Reset(float initialSpeedMultiplier)
        {
            LocalVelocity = Vector3.zero;
            currentSpeedMultiplier = initialSpeedMultiplier;
            speedMultiplierTransitionStart = initialSpeedMultiplier;
            speedMultiplierTransitionTarget = initialSpeedMultiplier;
            speedMultiplierTransitionElapsed = SpeedMultiplierTransitionTime;
            movementInputWasActive = false;
            stoppingVelocityChangeRate = 0f;
        }

        internal Vector3 Update(Vector3 localInput, float baseSpeed, float requestedSpeedMultiplier, float deltaTime)
        {
            bool speedTransitionActive = UpdateSpeedMultiplier(requestedSpeedMultiplier, deltaTime);
            if (localInput.sqrMagnitude > 1f)
            {
                localInput.Normalize();
            }

            bool movementInputActive = localInput.sqrMagnitude >= MovementInputEpsilonSquared;
            if (!movementInputActive)
            {
                localInput = Vector3.zero;
                if (movementInputWasActive)
                {
                    // Set once on release, so the stop takes the same time from any speed.
                    stoppingVelocityChangeRate = LocalVelocity.magnitude / VelocityTransitionTime;
                }
            }

            Vector3 targetVelocity = localInput * baseSpeed * currentSpeedMultiplier;
            if (movementInputActive)
            {
                float speedMultiplier = Mathf.Max(currentSpeedMultiplier, requestedSpeedMultiplier);
                bool inputOpposesVelocity = Vector3.Dot(LocalVelocity, targetVelocity) < 0f;
                if (inputOpposesVelocity)
                {
                    // Input against the motion: brake to a stop first.
                    float brakingRate = baseSpeed * speedMultiplier / OppositeInputDecelerationTime;
                    LocalVelocity = Vector3.MoveTowards(LocalVelocity, Vector3.zero, brakingRate * deltaTime);
                }
                else
                {
                    float changeRate = baseSpeed * speedMultiplier / VelocityTransitionTime;
                    if (speedTransitionActive)
                    {
                        float transitionRate = baseSpeed * Mathf.Abs(speedMultiplierTransitionTarget - speedMultiplierTransitionStart) / SpeedMultiplierTransitionTime;
                        changeRate = Mathf.Max(changeRate, transitionRate);
                    }

                    LocalVelocity = Vector3.MoveTowards(LocalVelocity, targetVelocity, changeRate * deltaTime);
                }
            }
            else
            {
                LocalVelocity = Vector3.MoveTowards(LocalVelocity, targetVelocity, stoppingVelocityChangeRate * deltaTime);
            }

            movementInputWasActive = movementInputActive;
            return LocalVelocity;
        }

        // Drops the part of the velocity that runs against a correction the constraints made.
        internal void RemoveBlockedComponent(Quaternion cameraRotation, Vector3 constraintCorrection)
        {
            if (constraintCorrection.sqrMagnitude < CollisionCorrectionEpsilonSquared)
            {
                return;
            }

            Vector3 correctionDirection = constraintCorrection.normalized;
            Vector3 worldVelocity = cameraRotation * LocalVelocity;
            float blockedSpeed = Vector3.Dot(worldVelocity, correctionDirection);
            if (blockedSpeed >= 0f)
            {
                return;
            }

            worldVelocity -= correctionDirection * blockedSpeed;
            LocalVelocity = Quaternion.Inverse(cameraRotation) * worldVelocity;
        }

        // Returns true while the Shift/Alt multiplier is still moving to its new value.
        private bool UpdateSpeedMultiplier(float requestedSpeedMultiplier, float deltaTime)
        {
            if (!Mathf.Approximately(requestedSpeedMultiplier, speedMultiplierTransitionTarget))
            {
                speedMultiplierTransitionStart = currentSpeedMultiplier;
                speedMultiplierTransitionTarget = requestedSpeedMultiplier;
                speedMultiplierTransitionElapsed = 0f;
            }

            if (speedMultiplierTransitionElapsed >= SpeedMultiplierTransitionTime)
            {
                currentSpeedMultiplier = speedMultiplierTransitionTarget;
                return false;
            }

            speedMultiplierTransitionElapsed = Mathf.Min(speedMultiplierTransitionElapsed + deltaTime, SpeedMultiplierTransitionTime);
            currentSpeedMultiplier = Mathf.Lerp(speedMultiplierTransitionStart, speedMultiplierTransitionTarget, speedMultiplierTransitionElapsed / SpeedMultiplierTransitionTime);
            return true;
        }
    }

    // Keeps a camera position within reach of the boat and above the water.
    internal sealed class CameraConstraintResolver
    {
        private readonly FreeCameraAndCollisionPlugin plugin;
        private readonly UnityEngine.Object queryContext;
        private readonly SampleHeightHelper waterHeightSampler = new SampleHeightHelper();

        internal CameraConstraintResolver(FreeCameraAndCollisionPlugin ownerPlugin, UnityEngine.Object owner)
        {
            plugin = ownerPlugin;
            queryContext = owner;
        }

        internal Vector3 ConstrainFreeCameraPosition(Vector3 position, Vector3 focus, float maximumDistance)
        {
            position = ClampToMaximumDistance(position, focus, maximumDistance);
            position = ApplyWaterCollision(position);
            Vector3 offset = position - focus;
            float maximumDistanceSquared = maximumDistance * maximumDistance;
            if (offset.sqrMagnitude <= maximumDistanceSquared)
            {
                return position;
            }

            // The water pushed the position back out of reach: keep its height and pull it in level.
            float heightOffset = position.y - focus.y;
            if (Mathf.Abs(heightOffset) >= maximumDistance)
            {
                return focus + Vector3.up * Mathf.Sign(heightOffset) * maximumDistance;
            }

            Vector3 horizontalOffset = new Vector3(offset.x, 0f, offset.z);
            float maximumHorizontalDistance = Mathf.Sqrt(maximumDistanceSquared - heightOffset * heightOffset);
            if (horizontalOffset.sqrMagnitude > maximumHorizontalDistance * maximumHorizontalDistance)
            {
                horizontalOffset = horizontalOffset.normalized * maximumHorizontalDistance;
            }

            return focus + horizontalOffset + Vector3.up * heightOffset;
        }

        private Vector3 ClampToMaximumDistance(Vector3 position, Vector3 focus, float maximumDistance)
        {
            Vector3 offset = position - focus;
            if (offset.sqrMagnitude <= maximumDistance * maximumDistance)
            {
                return position;
            }

            return focus + offset.normalized * maximumDistance;
        }

        // Raises the position to the sphere radius plus the padding above the waves at that spot.
        internal Vector3 ApplyWaterCollision(Vector3 position)
        {
            if (!plugin.WaterCollision.Value || OceanRenderer.Instance == null)
            {
                return position;
            }

            waterHeightSampler.Init(position, plugin.SphereRadius.Value * 2f, true, queryContext);
            if (!waterHeightSampler.Sample(out float waterHeight))
            {
                return position;
            }

            float minimumHeight = waterHeight + plugin.SphereRadius.Value + plugin.SurfacePadding.Value;
            if (position.y < minimumHeight)
            {
                position.y = minimumHeight;
            }

            return position;
        }
    }

    // While free camera is active, the game's own input reads answer "nothing pressed". The prefixes run after other
    // mods' prefixes, so none of those replaces that answer.
    internal static class GameInputSuppression
    {
        internal static bool AllowOriginalOrNeutralize(ref bool result)
        {
            if (!FreeCameraController.IsInputCaptured)
            {
                return true;
            }

            result = false;
            return false;
        }

        internal static bool AllowOriginalOrNeutralize(ref float result)
        {
            if (!FreeCameraController.IsInputCaptured)
            {
                return true;
            }

            result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetKey")]
    internal static class GameInputGetKeyPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ref bool __result)
        {
            return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetKeyDown")]
    internal static class GameInputGetKeyDownPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ref bool __result)
        {
            return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetKeyUp")]
    internal static class GameInputGetKeyUpPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ref bool __result)
        {
            return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetScrollAxis")]
    internal static class GameInputGetScrollAxisPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ref float __result)
        {
            return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetPrimaryHorizontal")]
    internal static class GameInputGetPrimaryHorizontalPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ref float __result)
        {
            return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetPrimaryVertical")]
    internal static class GameInputGetPrimaryVerticalPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ref float __result)
        {
            return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetSecondaryHorizontal")]
    internal static class GameInputGetSecondaryHorizontalPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ref float __result)
        {
            return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
        }
    }

    [HarmonyPatch(typeof(GameInput), "GetSecondaryVertical")]
    internal static class GameInputGetSecondaryVerticalPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(ref float __result)
        {
            return GameInputSuppression.AllowOriginalOrNeutralize(ref __result);
        }
    }

    [HarmonyPatch(typeof(BoatCamera), "Start")]
    internal static class BoatCameraStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(BoatCamera __instance)
        {
            FreeCameraAndCollisionPlugin.Instance?.AttachController(__instance);
        }
    }

    // Leaves free camera before the game puts the camera back on the player.
    [HarmonyPatch(typeof(BoatCamera), "SwitchOff")]
    internal static class BoatCameraSwitchOffPatch
    {
        [HarmonyPrefix]
        private static void Prefix(BoatCamera __instance)
        {
            FreeCameraController.ExitIfOwnedBy(__instance);
        }
    }
}
