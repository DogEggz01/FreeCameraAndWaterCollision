using UnityEngine;

namespace FreeCameraAndCollision
{
	internal sealed class DroneVelocityState
	{
		private const float VelocityTransitionTime = 2f;
		private const float SpeedMultiplierTransitionTime = 1f;
		private const float OppositeInputDecelerationTime = 1f;
		private const float MovementInputEpsilonSquared = 0.0001f;
		private const float CollisionCorrectionEpsilonSquared = 0.000001f;

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
			speedMultiplierTransitionElapsed =
				SpeedMultiplierTransitionTime;
			movementInputWasActive = false;
			stoppingVelocityChangeRate = 0f;
		}

		internal Vector3 Update(
			Vector3 localInput,
			float baseSpeed,
			float requestedSpeedMultiplier,
			float deltaTime)
		{
			bool speedMultiplierTransitionActive =
				UpdateSpeedMultiplier(requestedSpeedMultiplier, deltaTime);

			if (localInput.sqrMagnitude > 1f)
			{
				localInput.Normalize();
			}

			bool movementInputActive =
				localInput.sqrMagnitude >= MovementInputEpsilonSquared;
			if (!movementInputActive)
			{
				localInput = Vector3.zero;
				if (movementInputWasActive)
				{
					stoppingVelocityChangeRate =
						LocalVelocity.magnitude / VelocityTransitionTime;
				}
			}

			Vector3 targetVelocity =
				localInput * baseSpeed * currentSpeedMultiplier;
			if (movementInputActive)
			{
				float maximumSpeedMultiplier = Mathf.Max(
					currentSpeedMultiplier,
					requestedSpeedMultiplier);
				bool oppositeDirectionPressed =
					Vector3.Dot(LocalVelocity, targetVelocity) < 0f;

				if (oppositeDirectionPressed)
				{
					float brakingRate =
						baseSpeed * maximumSpeedMultiplier /
						OppositeInputDecelerationTime;

					LocalVelocity = Vector3.MoveTowards(
						LocalVelocity,
						Vector3.zero,
						brakingRate * deltaTime);
				}
				else
				{
					float velocityChangeRate =
						baseSpeed * maximumSpeedMultiplier /
						VelocityTransitionTime;

					if (speedMultiplierTransitionActive)
					{
						float modifierVelocityChangeRate =
							baseSpeed *
							Mathf.Abs(
								speedMultiplierTransitionTarget -
								speedMultiplierTransitionStart) /
							SpeedMultiplierTransitionTime;
						velocityChangeRate = Mathf.Max(
							velocityChangeRate,
							modifierVelocityChangeRate);
					}

					LocalVelocity = Vector3.MoveTowards(
						LocalVelocity,
						targetVelocity,
						velocityChangeRate * deltaTime);
				}
			}
			else
			{
				LocalVelocity = Vector3.MoveTowards(
					LocalVelocity,
					targetVelocity,
					stoppingVelocityChangeRate * deltaTime);
			}
			movementInputWasActive = movementInputActive;

			return LocalVelocity;
		}

		internal void RemoveBlockedComponent(
			Quaternion cameraRotation,
			Vector3 constraintCorrection)
		{
			if (constraintCorrection.sqrMagnitude <
				CollisionCorrectionEpsilonSquared)
			{
				return;
			}

			Vector3 correctionDirection = constraintCorrection.normalized;
			Vector3 worldVelocity = cameraRotation * LocalVelocity;
			float blockedSpeed = Vector3.Dot(
				worldVelocity,
				correctionDirection);

			if (blockedSpeed >= 0f)
			{
				return;
			}

			worldVelocity -= correctionDirection * blockedSpeed;
			LocalVelocity = Quaternion.Inverse(cameraRotation) * worldVelocity;
		}

		private bool UpdateSpeedMultiplier(
			float requestedSpeedMultiplier,
			float deltaTime)
		{
			if (!Mathf.Approximately(
				requestedSpeedMultiplier,
				speedMultiplierTransitionTarget))
			{
				speedMultiplierTransitionStart = currentSpeedMultiplier;
				speedMultiplierTransitionTarget = requestedSpeedMultiplier;
				speedMultiplierTransitionElapsed = 0f;
			}

			if (speedMultiplierTransitionElapsed >=
				SpeedMultiplierTransitionTime)
			{
				currentSpeedMultiplier = speedMultiplierTransitionTarget;
				return false;
			}

			speedMultiplierTransitionElapsed = Mathf.Min(
				speedMultiplierTransitionElapsed + deltaTime,
				SpeedMultiplierTransitionTime);
			currentSpeedMultiplier = Mathf.Lerp(
				speedMultiplierTransitionStart,
				speedMultiplierTransitionTarget,
				speedMultiplierTransitionElapsed /
				SpeedMultiplierTransitionTime);

			return true;
		}
	}
}
