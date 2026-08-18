using HarmonyLib;

namespace FreeCameraAndCollision
{
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
}
