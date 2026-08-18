using UnityEngine;

namespace FreeCameraAndCollision
{
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

		internal Quaternion MovementRotation
		{
			get
			{
				if (Mode != FreeCameraMovementMode.Drone)
				{
					return Quaternion.Euler(-worldPitch, worldYaw, 0f);
				}

				return Rotation;
			}
		}

		internal void Initialize(
			Quaternion initialRotation,
			FreeCameraMovementMode mode,
			float minimumWorldPitch,
			float maximumWorldPitch)
		{
			Rotation = initialRotation;
			Mode = mode;
			minimumPitch = minimumWorldPitch;
			maximumPitch = maximumWorldPitch;
			rollResetActive = false;
			UpdateWorldAnglesFromRotation();
		}

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

		internal void UpdateLook(
			float yawDelta,
			float mousePitchDelta,
			float controllerPitchDelta,
			bool invertDroneMousePitch,
			float rollInput,
			float rollingSpeed,
			bool resetRollPressed,
			float deltaTime)
		{
			float mousePitchMultiplier =
				Mode == FreeCameraMovementMode.Drone && invertDroneMousePitch
					? -1f
					: 1f;
			float pitchDelta =
				controllerPitchDelta +
				mousePitchDelta * mousePitchMultiplier;
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

		private void UpdateWorldRotation(
			float yawDelta,
			float pitchDelta,
			float rollDelta,
			float deltaTime)
		{
			worldYaw = NormalizeAngle(worldYaw + yawDelta);
			worldPitch = Mathf.Clamp(
				worldPitch + pitchDelta,
				minimumPitch,
				maximumPitch);

			if (rollResetActive)
			{
				worldRoll = Mathf.MoveTowardsAngle(
					worldRoll,
					0f,
					RollResetSpeedDegreesPerSecond * deltaTime);
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

		private void UpdateDroneRotation(
			float yawDelta,
			float pitchDelta,
			float rollDelta,
			float deltaTime)
		{
			Quaternion localRotation =
				Quaternion.Euler(-pitchDelta, yawDelta, rollDelta);
			Rotation = (Rotation * localRotation).normalized;

			if (rollResetActive)
			{
				UpdateDroneRollReset(deltaTime);
			}
		}

		private void UpdateWorldAnglesFromRotation()
		{
			Vector3 angles = Rotation.eulerAngles;
			worldYaw = NormalizeAngle(angles.y);
			worldPitch = Mathf.Clamp(
				-NormalizeAngle(angles.x),
				minimumPitch,
				maximumPitch);
			worldRoll = NormalizeAngle(angles.z);
		}

		private void UpdateDroneRollReset(float deltaTime)
		{
			Vector3 forward = Rotation * Vector3.forward;
			Vector3 currentUp = Rotation * Vector3.up;
			Vector3 targetUp = Vector3.ProjectOnPlane(Vector3.up, forward);

			if (targetUp.sqrMagnitude < RollReferenceEpsilonSquared)
			{
				targetUp = Vector3.ProjectOnPlane(Vector3.forward, forward);
			}
			if (targetUp.sqrMagnitude < RollReferenceEpsilonSquared)
			{
				return;
			}

			targetUp.Normalize();
			float currentRoll = Vector3.SignedAngle(
				targetUp,
				currentUp,
				forward);
			float nextRoll = Mathf.MoveTowardsAngle(
				currentRoll,
				0f,
				RollResetSpeedDegreesPerSecond * deltaTime);
			float rollCorrection = Mathf.DeltaAngle(currentRoll, nextRoll);

			Rotation = (
				Rotation * Quaternion.AngleAxis(rollCorrection, Vector3.forward))
				.normalized;
			if (nextRoll == 0f)
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
}
