using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FreeCameraAndCollision
{
	[DefaultExecutionOrder(10000)]
	internal sealed class FreeCameraController : MonoBehaviour
	{
		private const KeyCode ToggleKey = KeyCode.B;
		private const KeyCode FollowShipToggleKey = KeyCode.H;
		private const KeyCode ControllerToggleKey = KeyCode.JoystickButton8;
		private const KeyCode ControllerDescendKey = KeyCode.JoystickButton4;
		private const KeyCode ControllerRiseKey = KeyCode.JoystickButton5;

		private const float MoveSpeed = 10f;
		private const float SpeedBoostMultiplier = 1.5f;
		private const float MinimumZoom = 20f;
		private const float MouseZoomSpeed = 30f;
		private const float ControllerZoomSpeed = 35f;
		private const float ZoomResponseSpeed = 100f;
		private const float WorldShiftDistanceThreshold = 20f;

		private const string PrimaryHorizontalAxis =
			"Oculus_CrossPlatform_PrimaryThumbstickHorizontal";
		private const string PrimaryVerticalAxis =
			"Oculus_CrossPlatform_PrimaryThumbstickVertical";
		private const string SecondaryHorizontalAxis =
			"Oculus_CrossPlatform_SecondaryThumbstickHorizontal";
		private const string SecondaryVerticalAxis =
			"Oculus_CrossPlatform_SecondaryThumbstickVertical";
		private const string PrimaryTriggerAxis =
			"Oculus_CrossPlatform_PrimaryIndexTrigger";
		private const string SecondaryTriggerAxis =
			"Oculus_CrossPlatform_SecondaryIndexTrigger";
		private const string MouseHorizontalAxis = "Mouse X";
		private const string MouseVerticalAxis = "Mouse Y";
		private const string MouseScrollAxis = "Mouse ScrollWheel";

		private static readonly FieldInfo CenterEyeField =
			AccessTools.Field(typeof(BoatCamera), "centerEye");
		private static readonly FieldInfo PlayerLooksField =
			AccessTools.Field(typeof(BoatCamera), "playerLooks");
		private static readonly FieldInfo BoatLooksField =
			AccessTools.Field(typeof(BoatCamera), "boatLooks");

		private BoatCamera boatCamera;
		private FreeCameraAndCollisionPlugin plugin;
		private CameraConstraintResolver constraintResolver;
		private Transform centerEye;
		private Camera unityCamera;
		private MouseLook[] playerLooks;
		private MouseLook[] boatLooks;
		private bool[] savedBoatLookStates;

		private bool initialized;
		private bool freeCameraActive;
		private Transform followedBoat;
		private bool followShipTranslation;
		private Vector3 lastBoatPosition;
		private Vector3 freeCameraPosition;
		private Quaternion freeCameraRotation;

		private float lookYaw;
		private float lookPitchAccumulator;
		private float lookSensitivityX = 2f;
		private float lookSensitivityY = 2f;
		private float minimumLookY = -80f;
		private float maximumLookY = 80f;

		private Vector3 savedCenterEyeLocalPosition;
		private Quaternion savedCenterEyeLocalRotation;
		private float savedZoom;
		private float targetZoom;

		private OVRPlayerController playerController;
		private bool savedPlayerControllerEnabled;

		internal static FreeCameraController ActiveController { get; private set; }

		internal static bool IsInputCaptured
		{
			get
			{
				return ActiveController != null &&
					ActiveController.freeCameraActive;
			}
		}

		internal void Initialize(
			BoatCamera owner,
			FreeCameraAndCollisionPlugin ownerPlugin)
		{
			if (initialized &&
				boatCamera == owner &&
				plugin == ownerPlugin)
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
				ownerPlugin.LogInitializationError(
					"Free camera could not initialize: BoatCamera.centerEye was not found.");
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

			constraintResolver =
				new CameraConstraintResolver(ownerPlugin, this);
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

			bool togglePressed =
				Input.GetKeyDown(ToggleKey) ||
				GameInput.controllerEnabled &&
				Input.GetKeyDown(ControllerToggleKey);

			if (!freeCameraActive)
			{
				if (togglePressed && CanEnterFreeCamera())
				{
					EnterFreeCamera();
				}

				return;
			}

			if (togglePressed || !CanRemainActive())
			{
				ExitFreeCamera();
				return;
			}

			UpdateFreeCameraInput();
		}

		private void LateUpdate()
		{
			if (!initialized || !BoatCamera.on || GameState.currentBoat == null)
			{
				return;
			}

			if (freeCameraActive)
			{
				centerEye.position = freeCameraPosition;
				centerEye.rotation = freeCameraRotation;
				return;
			}

			ApplyThirdPersonWaterConstraint();
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

		private bool CanEnterFreeCamera()
		{
			return BoatCamera.on &&
				GameState.playing &&
				!GameState.currentlyLoading &&
				GameState.currentBoat != null &&
				GameState.currentShipyard == null &&
				!GameState.inCursorMenu &&
				!GameState.VREnabled;
		}

		private bool CanRemainActive()
		{
			return BoatCamera.on &&
				GameState.playing &&
				!GameState.currentlyLoading &&
				GameState.currentBoat != null &&
				followedBoat != null &&
				GameState.currentBoat == followedBoat &&
				GameState.currentShipyard == null &&
				!GameState.inCursorMenu &&
				!GameState.VREnabled;
		}

		private void EnterFreeCamera()
		{
			if (ActiveController != null && ActiveController != this)
			{
				ActiveController.ExitFreeCamera();
			}

			followedBoat = GameState.currentBoat;
			followShipTranslation = true;
			lastBoatPosition = followedBoat.position;
			freeCameraPosition = centerEye.position;
			freeCameraRotation = centerEye.rotation;

			Vector3 angles = freeCameraRotation.eulerAngles;
			lookYaw = angles.y;
			lookPitchAccumulator = -NormalizeAngle(angles.x);
			lookPitchAccumulator =
				Mathf.Clamp(
					lookPitchAccumulator,
					minimumLookY,
					maximumLookY);

			savedCenterEyeLocalPosition = centerEye.localPosition;
			savedCenterEyeLocalRotation = centerEye.localRotation;

			if (unityCamera != null)
			{
				savedZoom = unityCamera.fieldOfView;
				targetZoom = savedZoom;
			}

			SaveAndDisableBoatLook();
			SaveAndDisablePlayerMovement();

			ActiveController = this;
			freeCameraActive = true;
		}

		private void ExitFreeCamera()
		{
			if (!freeCameraActive)
			{
				return;
			}

			freeCameraActive = false;

			if (centerEye != null)
			{
				centerEye.localPosition = savedCenterEyeLocalPosition;
				centerEye.localRotation = savedCenterEyeLocalRotation;
			}

			if (unityCamera != null)
			{
				unityCamera.fieldOfView = savedZoom;
			}

			RestoreBoatLook();
			RestorePlayerMovement();
			followedBoat = null;

			if (ActiveController == this)
			{
				ActiveController = null;
			}
		}

		private void UpdateFreeCameraInput()
		{
			float deltaTime = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

			if (Input.GetKeyDown(FollowShipToggleKey))
			{
				followShipTranslation = !followShipTranslation;
			}

			UpdateFreeCameraLook();
			UpdateFreeCameraMovement(deltaTime);
			UpdateFreeCameraZoom(deltaTime);
		}

		private void UpdateFreeCameraLook()
		{
			float controllerHorizontal = 0f;
			float controllerVertical = 0f;

			if (GameInput.controllerEnabled)
			{
				controllerHorizontal = Input.GetAxis(SecondaryHorizontalAxis);
				controllerVertical = Input.GetAxis(SecondaryVerticalAxis);
			}

			float mouseHorizontal = Input.GetAxis(MouseHorizontalAxis);
			float mouseVertical = Input.GetAxis(MouseVerticalAxis);

			if (controllerHorizontal != 0f || controllerVertical != 0f)
			{
				GameState.lookingWithController = true;
			}
			if (mouseHorizontal != 0f || mouseVertical != 0f)
			{
				GameState.lookingWithController = false;
			}

			float sensitivityMultiplier =
				(float)Settings.mouseSens / 5f + 0.1f;

			lookYaw +=
				(controllerHorizontal + mouseHorizontal) *
				lookSensitivityX *
				sensitivityMultiplier;
			lookPitchAccumulator +=
				(controllerVertical + mouseVertical) *
				lookSensitivityY *
				sensitivityMultiplier;

			lookPitchAccumulator =
				Mathf.Clamp(
					lookPitchAccumulator,
					minimumLookY,
					maximumLookY);

			freeCameraRotation =
				Quaternion.Euler(-lookPitchAccumulator, lookYaw, 0f);
		}

		private void UpdateFreeCameraMovement(float deltaTime)
		{
			Vector3 boatPosition = followedBoat.position;
			Vector3 boatMovement = boatPosition - lastBoatPosition;
			lastBoatPosition = boatPosition;

			if (boatMovement.sqrMagnitude >
				WorldShiftDistanceThreshold * WorldShiftDistanceThreshold)
			{
				freeCameraPosition += boatMovement;
				boatMovement = Vector3.zero;
			}
			else if (!followShipTranslation)
			{
				boatMovement = Vector3.zero;
			}

			float forward = GetKeyboardAxis(KeyCode.W, KeyCode.S);
			float strafe = GetKeyboardAxis(KeyCode.D, KeyCode.A);
			float vertical = GetKeyboardAxis(KeyCode.Space, KeyCode.LeftControl);

			if (Input.GetKey(KeyCode.RightControl))
			{
				vertical -= 1f;
			}

			if (GameInput.controllerEnabled)
			{
				forward += Input.GetAxis(PrimaryVerticalAxis);
				strafe += Input.GetAxis(PrimaryHorizontalAxis);

				if (Input.GetKey(ControllerRiseKey))
				{
					vertical += 1f;
				}
				if (Input.GetKey(ControllerDescendKey))
				{
					vertical -= 1f;
				}
			}

			Vector3 inputMovement =
				freeCameraRotation * Vector3.forward * forward +
				freeCameraRotation * Vector3.right * strafe +
				Vector3.up * vertical;

			if (inputMovement.sqrMagnitude > 1f)
			{
				inputMovement.Normalize();
			}

			bool speedBoosted =
				Input.GetKey(KeyCode.LeftShift) ||
				Input.GetKey(KeyCode.RightShift);
			float movementSpeed =
				MoveSpeed * (speedBoosted ? SpeedBoostMultiplier : 1f);

			Vector3 focus = boatPosition;
			Vector3 targetPosition =
				freeCameraPosition +
				boatMovement +
				inputMovement * movementSpeed * deltaTime;

			freeCameraPosition =
				constraintResolver.ConstrainFreeCameraPosition(
					targetPosition,
					focus,
					CameraDistanceLimits.MaximumDistanceFromBoat);
		}

		private void UpdateFreeCameraZoom(float deltaTime)
		{
			if (unityCamera == null)
			{
				return;
			}

			float mouseZoom = Input.GetAxis(MouseScrollAxis);
			float controllerZoom = 0f;

			if (GameInput.controllerEnabled)
			{
				controllerZoom =
					Input.GetAxis(SecondaryTriggerAxis) -
					Input.GetAxis(PrimaryTriggerAxis);
			}

			targetZoom -= mouseZoom * MouseZoomSpeed;
			targetZoom -= controllerZoom * ControllerZoomSpeed * deltaTime;
			targetZoom = Mathf.Clamp(targetZoom, MinimumZoom, savedZoom);

			unityCamera.fieldOfView = Mathf.MoveTowards(
				unityCamera.fieldOfView,
				targetZoom,
				ZoomResponseSpeed * deltaTime);
		}

		private void ApplyThirdPersonWaterConstraint()
		{
			centerEye.position =
				constraintResolver.ApplyWaterCollision(centerEye.position);
		}

		private void CacheVanillaFirstPersonLookSettings()
		{
			if (playerLooks == null)
			{
				return;
			}

			for (int i = 0; i < playerLooks.Length; i++)
			{
				MouseLook mouseLook = playerLooks[i];
				if (mouseLook == null)
				{
					continue;
				}

				if (mouseLook.axes == MouseLook.RotationAxes.MouseX ||
					mouseLook.axes == MouseLook.RotationAxes.MouseXAndY)
				{
					lookSensitivityX = mouseLook.sensitivityX;
				}

				if (mouseLook.axes == MouseLook.RotationAxes.MouseY ||
					mouseLook.axes == MouseLook.RotationAxes.MouseXAndY)
				{
					lookSensitivityY = mouseLook.sensitivityY;
					minimumLookY = mouseLook.minimumY;
					maximumLookY = mouseLook.maximumY;
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
				MouseLook mouseLook = boatLooks[i];
				if (mouseLook == null)
				{
					continue;
				}

				savedBoatLookStates[i] = mouseLook.enabled;
				mouseLook.enabled = false;
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
			float value = 0f;
			if (Input.GetKey(positive))
			{
				value += 1f;
			}
			if (Input.GetKey(negative))
			{
				value -= 1f;
			}

			return value;
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
}
