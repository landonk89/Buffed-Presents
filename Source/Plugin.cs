using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace com.redcrowbar.buffedpresents
{
    /// <summary>
    /// BuffedPresents - Makes the GiftBoxItem give either a value increase of the contents, or a random buyable item
    /// </summary>
    [BepInPlugin("com.redcrowbar.buffedpresents", "Buffed Presents", "0.0.3")]
    [BepInIncompatibility("GiftBoxRevert")]
    [BepInIncompatibility("LethalPresents")]
    [BepInIncompatibility("ExplodingPresents")]
    public class BuffedPresents : BaseUnityPlugin
    {
        public static ManualLogSource BPLogger = BepInEx.Logging.Logger.CreateLogSource("BuffedPresents");
        public static BuffedPresents Instance;
        public static ConfigEntry<bool> isEnabled;
        public static ConfigEntry<float> buyableItemChance; //percent chance 0-100 to spawn a buyable item
        public static ConfigEntry<float> minValueMultiply; //min random item value increase multiplier
        public static ConfigEntry<float> maxValueMultiply; //max random item value increase multiplier
        public const string mainConfigDesc = "Settings (only HOST controls these)";
        public const int targetGameVersion = 45; //current game build late dec 2023
        public static bool versionWarning = true;
        public static bool isDebugBuild = false;

        public bool IsDebugBuild()
        {
#if DEBUG
            if (!isDebugBuild)
                isDebugBuild = true;
#endif
            return isDebugBuild;
        }

        private void Awake()
        {
            Instance = this;
            isEnabled = Config.Bind(mainConfigDesc, "Enabled", true, "Enable/disable the plugin");
            buyableItemChance = Config.Bind(mainConfigDesc, "Percent chance present is a random buyable item", 50f, new ConfigDescription("The minimum multiplier to add to a present's value", new AcceptableValueRange<float>(0f, 100f)));
            minValueMultiply = Config.Bind(mainConfigDesc, "Min present value multiplier", 1.1f, new ConfigDescription("The min multiplier to add to a present's value, example: orig-value:50 x Multiplier 1.5 = new-value:75", new AcceptableValueRange<float>(1f, 10f)));
            maxValueMultiply = Config.Bind(mainConfigDesc, "Max present value multiplier", 2.0f, new ConfigDescription("The max multiplier to add to a present's value, example: orig-value:50 x Multiplier 1.5 = new-value:75", new AcceptableValueRange<float>(1f, 10f)));

            if (isEnabled.Value == false)
                return;

            //enable GiftBoxItem patch
            Harmony patcher = new("com.redcrowbar.buffedpresents");
            patcher.PatchAll(typeof(GiftBoxPatch));
        }

        private void Update()
        {
            //only continue if in-game
            if (!HUDManager.Instance || !isEnabled.Value)
                return;

            //check game version, warn if different than expected
            if (versionWarning && GameNetworkManager.Instance.gameVersionNum != targetGameVersion)
            {
                BPLogger.LogWarning($"WARNING: Game version is {GameNetworkManager.Instance.gameVersionNum}, expected {targetGameVersion}. You may encounter issues!");
                versionWarning = false;
            }

            //just for testing, press F8 to copy a giftbox from the level and spawn it at the player
            if (IsDebugBuild() && !StartOfRound.Instance.inShipPhase && Keyboard.current.f8Key.wasPressedThisFrame)
            {
                foreach (var scrapItem in RoundManager.Instance.currentLevel.spawnableScrap)
                {
                    if (scrapItem.spawnableItem.itemName.Contains("Gift"))
                    {
                        BPLogger.LogDebug("Found GiftBox");
                        var parent = RoundManager.Instance.spawnedScrapContainer;
                        var localPlayer = StartOfRound.Instance.localPlayerController;
                        Vector3 vector = localPlayer.transform.position + localPlayer.transform.forward * 0.5f + Vector3.up * 0.5f;
                        GameObject newBox = Instantiate(scrapItem.spawnableItem.spawnPrefab, vector, Quaternion.identity, parent);
                        GiftBoxItem component = newBox.GetComponent<GiftBoxItem>();
                        component.NetworkObject.Spawn();
                        break;
                    }
                }
            }
        }
    }

    public class GiftBoxPatch
    {
        public static ManualLogSource PatchLogger = BepInEx.Logging.Logger.CreateLogSource("BuffedPresentsPatches");

        //Roll based on user chance percentage setting
        public static bool CheckChance()
        {
            if (Mathf.Clamp(BuffedPresents.buyableItemChance.Value, 0f, 100f) / 100f > Random.Range(0f, 0.99f))
                return true;

            return false;
        }

        //protected NetworkBehaviour.__RpcExecStage
        public enum RpcExecStage
        {
            None,
            Server,
            Client
        }

        //from decompiled game code
        public static IEnumerator SetObjectToHitGroundSFX(GrabbableObject gObject)
        {
            yield return new WaitForEndOfFrame();
            PatchLogger.LogDebug("Setting " + gObject.itemProperties.itemName + " hit ground to false");
            gObject.reachedFloorTarget = false;
            gObject.hasHitGround = false;
            gObject.fallTime = 0f;
        }

        [HarmonyPatch(typeof(GiftBoxItem), nameof(GiftBoxItem.OpenGiftBoxServerRpc))]
        [HarmonyPrefix]
        public static bool PrefixRpc(GiftBoxItem __instance)
        {
            //If the check is false, keep the original item in the present but increase the value.
            //This is basically the original method modified heavily
            if (!CheckChance())
            {
                NetworkManager networkManager = __instance.NetworkManager;
                if (networkManager is null || !networkManager.IsListening)
                    return false;

                if (AccessExtensions.GetFieldValue<RpcExecStage>(__instance, "__rpc_exec_stage") != RpcExecStage.Server && (networkManager.IsClient || networkManager.IsHost))
                {
                    ServerRpcParams serverRpcParams = new();
                    FastBufferWriter fastBufferWriter = (FastBufferWriter)__instance.CallMethod("__beginSendServerRpc", 2878544999U, serverRpcParams, RpcDelivery.Reliable);
                    __instance.CallMethod("__endSendServerRpc", fastBufferWriter, 2878544999U, serverRpcParams, RpcDelivery.Reliable);
                }

                if (AccessExtensions.GetFieldValue<RpcExecStage>(__instance, "__rpc_exec_stage") != RpcExecStage.Server || (!networkManager.IsServer && !networkManager.IsHost))
                    return false;

                GameObject gameObject = null;
                int presentValue = 0;
                Vector3 spawnPosition = Vector3.zero;
                if (AccessExtensions.GetFieldValue<GameObject>(__instance, "objectInPresent") == null)
                {
                    PatchLogger.LogError("Error: There is no object in gift box!");
                }
                else
                {
                    Transform parent;
                    if (((__instance.playerHeldBy != null && __instance.playerHeldBy.isInElevator) || StartOfRound.Instance.inShipPhase) && RoundManager.Instance.spawnedScrapContainer != null)
                        parent = RoundManager.Instance.spawnedScrapContainer;
                    else
                        parent = StartOfRound.Instance.elevatorTransform;

                    //init stuff
                    spawnPosition = __instance.transform.position + Vector3.up * 0.25f;
                    gameObject = Object.Instantiate(AccessExtensions.GetFieldValue<GameObject>(__instance, "objectInPresent"), spawnPosition, Quaternion.identity, parent);
                    GrabbableObject component = gameObject.GetComponent<GrabbableObject>();
                    PlayerControllerB previousPlayerHeldBy = AccessExtensions.GetFieldValue<PlayerControllerB>(__instance, "previousPlayerHeldBy");

                    //set stuff
                    component.startFallingPosition = spawnPosition;
                    __instance.StartCoroutine(SetObjectToHitGroundSFX(component));
                    component.targetFloorPosition = component.GetItemFloorPosition(__instance.transform.position);
                    if (previousPlayerHeldBy != null && previousPlayerHeldBy.isInHangarShipRoom)
                        previousPlayerHeldBy.SetItemInElevator(droppedInShipRoom: true, droppedInElevator: true, component);

                    //like the original method, but randomly increase the value
                    presentValue = Mathf.RoundToInt(Random.Range(component.itemProperties.minValue + 25, component.itemProperties.maxValue + 35) * RoundManager.Instance.scrapValueMultiplier);
                    float multiplier = Random.Range(BuffedPresents.minValueMultiply.Value, BuffedPresents.maxValueMultiply.Value);
                    presentValue = Mathf.RoundToInt(multiplier * presentValue);
                    component.SetScrapValue(presentValue);
                    component.NetworkObject.Spawn();
                }

                if (gameObject != null)
                    __instance.OpenGiftBoxClientRpc(gameObject.GetComponent<NetworkObject>(), presentValue, spawnPosition);

                __instance.OpenGiftBoxNoPresentClientRpc();
                return false;
            }
            else //if the check passes, spawn a random buyable GrabbableObject instead, ex. flashlight, boombox, shovel, etc
            {
                NetworkManager networkManager = __instance.NetworkManager;
                if (networkManager is null || !networkManager.IsListening)
                {
                    return false;
                }

                if (AccessExtensions.GetFieldValue<RpcExecStage>(__instance, "__rpc_exec_stage") != RpcExecStage.Server && (networkManager.IsClient || networkManager.IsHost))
                {
                    ServerRpcParams serverRpcParams = new();
                    FastBufferWriter fastBufferWriter = (FastBufferWriter)__instance.CallMethod("__beginSendServerRpc", 2878544999U, serverRpcParams, RpcDelivery.Reliable);
                    __instance.CallMethod("__endSendServerRpc", fastBufferWriter, 2878544999U, serverRpcParams, RpcDelivery.Reliable);
                }

                if (AccessExtensions.GetFieldValue<RpcExecStage>(__instance, "__rpc_exec_stage") != RpcExecStage.Server || (!networkManager.IsServer && !networkManager.IsHost))
                    return false;

                Transform parent;
                if (((__instance.playerHeldBy != null && __instance.playerHeldBy.isInElevator) || StartOfRound.Instance.inShipPhase) && RoundManager.Instance.spawnedScrapContainer != null)
                    parent = RoundManager.Instance.spawnedScrapContainer;
                else
                    parent = StartOfRound.Instance.elevatorTransform;

                //this is where important stuff differs from the original
                //initialize stuff
                Vector3 spawnPosition = __instance.transform.position + Vector3.up * 0.25f;
                Terminal terminalScript = Object.FindObjectOfType<Terminal>();
                Item item = terminalScript.buyableItemsList[Random.Range(0, terminalScript.buyableItemsList.Length)];
                GameObject objectInPresent = Object.Instantiate(item.spawnPrefab, spawnPosition, Quaternion.identity, parent);
                GrabbableObject component = objectInPresent.GetComponent<GrabbableObject>();
                PlayerControllerB previousPlayerHeldBy = AccessExtensions.GetFieldValue<PlayerControllerB>(__instance, "previousPlayerHeldBy");

                //start setting stuff and spawn it
                component.startFallingPosition = spawnPosition;
                __instance.StartCoroutine(SetObjectToHitGroundSFX(component));
                component.targetFloorPosition = component.GetItemFloorPosition(__instance.transform.position);
                if (previousPlayerHeldBy != null && previousPlayerHeldBy.isInHangarShipRoom)
                    previousPlayerHeldBy.SetItemInElevator(droppedInShipRoom: true, droppedInElevator: true, component);

                component.NetworkObject.Spawn();

                if (objectInPresent != null)
                    __instance.OpenGiftBoxClientRpc(objectInPresent.GetComponent<NetworkObject>(), 0, spawnPosition);

                __instance.OpenGiftBoxNoPresentClientRpc();
                return false;
            }
        }
    }
}
