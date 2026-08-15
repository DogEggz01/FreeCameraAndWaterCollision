using HarmonyLib;
using UnityEngine;

namespace FreeCameraAndCollision
{
	internal static class CameraDistanceLimits
	{
		internal const float MaximumDistanceFromBoat = 80f;
		internal const float MinimumThirdPersonDistanceFromBoat = 8f;
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

	[HarmonyPatch(typeof(BoatCamera), "SwitchOff")]
	internal static class BoatCameraSwitchOffPatch
	{
		[HarmonyPrefix]
		private static void Prefix(BoatCamera __instance)
		{
			FreeCameraController.ExitIfOwnedBy(__instance);
		}
	}

	[HarmonyPatch(typeof(BoatCamera), "UpdateZoom")]
	internal static class BoatCameraUpdateZoomPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(
			ref float ___zoomLevel,
			float ___zoomSpeed)
		{
			___zoomLevel += GameInput.GetScrollAxis() * ___zoomSpeed;
			___zoomLevel = Mathf.Clamp(
				___zoomLevel,
				-CameraDistanceLimits.MaximumDistanceFromBoat,
				-CameraDistanceLimits.MinimumThirdPersonDistanceFromBoat);

			return false;
		}
	}
}
