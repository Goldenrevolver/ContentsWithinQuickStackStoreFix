﻿using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace ContentsWithin {
  [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
  public class ContentsWithin : BaseUnityPlugin {
    public const string PluginGUID = "com.maxsch.valheim.contentswithin";
    public const string PluginName = "ContentsWithin";
    public const string PluginVersion = "2.0.2";

    private static ConfigEntry<bool> _isModEnabled;
    private static ConfigEntry<KeyboardShortcut> _toggleShowContentsShortcut;
    private static ConfigEntry<float> openDelayTime;

    private static bool isRealGuiVisible;
    private static bool showContent = true;
    private static float delayTime;
    private static Inventory emptyInventory = new Inventory("", null, 0, 0);

    private static HashSet<InventoryGrid> initializedGrids = new HashSet<InventoryGrid>();

    private static Container _lastHoverContainer;
    private  static GameObject _lastHoverObject;

    private static GameObject _inventoryPanel;
    private static GameObject _infoPanel;
    private static GameObject _craftingPanel;
    private static GameObject _takeAllButton;

    private Harmony _harmony;

    public void Awake() {
      _isModEnabled = Config.Bind("_Global", "isModEnabled", true, "Globally enable or disable this mod.");

      _toggleShowContentsShortcut =
          Config.Bind(
              "Hotkeys",
              "toggleShowContentsShortcut",
              new KeyboardShortcut(KeyCode.P, KeyCode.RightShift),
              "Shortcut to toggle on/off the 'show container contents' feature.");

      openDelayTime = Config.Bind("Settings", "openDelayTime", 0.3f, "Time before the UI is closed when not hovering over a chest. This reduces the amount of animations when switching between chests.");

      _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), harmonyInstanceId: PluginGUID);
    }

    private void Update() {
      if (!_isModEnabled.Value) {
        return;
      }

      if (_toggleShowContentsShortcut.Value.IsDown()) {
        showContent = !showContent;

        if (MessageHud.instance) {
          MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"ShowContainerContents: {showContent}");
        }

        if (!showContent && !isRealGuiVisible && InventoryGui.instance) {
          InventoryGui.instance.Hide();
        }
      }
    }

    private static bool ShowRealGUI() {
      return !_isModEnabled.Value || !showContent || isRealGuiVisible;
    }

    [HarmonyPatch(typeof(Player))]
    public class PlayerPatch {
      [HarmonyPatch(nameof(Player.UpdateHover)), HarmonyPostfix]
      public static void UpdateHoverPostfix(Player __instance) {
        if (!_isModEnabled.Value || _lastHoverObject == __instance.m_hovering) {
          return;
        }

        _lastHoverObject = __instance.m_hovering;
        _lastHoverContainer = _lastHoverObject ? _lastHoverObject.GetComponentInParent<Container>() : null;
      }
    }

    [HarmonyPatch(typeof(GuiBar))]
    public class GuiBarPatch {
      [HarmonyPatch(nameof(GuiBar.SetValue)), HarmonyPrefix]
      public static void GuiBarSetValuePrefix(GuiBar __instance) {
        if (__instance.m_firstSet) {
          __instance.m_width = __instance.m_bar.sizeDelta.x;
        }
      }
    }

    [HarmonyPatch(typeof(InventoryGrid))]
    public static class InventoryGridPatch {
      [HarmonyPatch(nameof(InventoryGrid.Awake)), HarmonyPostfix]
      public static void AwakePostfix(InventoryGrid __instance) {
        initializedGrids.Add(__instance);
      }
    }

    [HarmonyPatch(typeof(InventoryGui))]
    public class InventoryGuiPatch {
      [HarmonyPatch(nameof(InventoryGui.Awake)), HarmonyPostfix, HarmonyPriority(Priority.Low)]
      public static void AwakePostfix(ref InventoryGui __instance) {
        _inventoryPanel = __instance.m_player.Ref()?.gameObject;
        _infoPanel = __instance.m_infoPanel.Ref()?.gameObject;
        _craftingPanel = __instance.m_inventoryRoot.Find("Crafting").Ref()?.gameObject;
        _takeAllButton = __instance.m_takeAllButton.Ref()?.gameObject;

        if (Chainloader.PluginInfos.ContainsKey("randyknapp.mods.auga")) {
          _craftingPanel = __instance.m_inventoryRoot.Find("RightPanel").Ref()?.gameObject;
        }
      }

      [HarmonyPatch(nameof(InventoryGui.Show)), HarmonyPostfix]
      public static void ShowPostfix() {
        isRealGuiVisible = true;
      }

      [HarmonyPatch(nameof(InventoryGui.Hide)), HarmonyPostfix]
      public static void HidePostfix() {
        isRealGuiVisible = false;
      }

      [HarmonyPatch(nameof(InventoryGui.Update)), HarmonyPrefix]
      public static void UpdatePrefix(InventoryGui __instance) {
        if (!ShowRealGUI()) {
          __instance.m_animator.SetBool("visible", false);
        }
      }

      [HarmonyPatch(nameof(InventoryGui.Update)), HarmonyPostfix]
      public static void UpdatePostfix(InventoryGui __instance) {
        _inventoryPanel.Ref()?.SetActive(ShowRealGUI());
        _craftingPanel.Ref()?.SetActive(ShowRealGUI());
        _infoPanel.Ref()?.SetActive(ShowRealGUI());
        _takeAllButton.Ref()?.SetActive(ShowRealGUI());

        if (ShowRealGUI()) {
          return;
        }

        if (HasContainerAccess(_lastHoverContainer)) {
          ShowPreviewContainer(_lastHoverContainer.GetInventory());
          delayTime = openDelayTime.Value;
        } else if (delayTime > 0) {
          ShowPreviewContainer(emptyInventory);
          delayTime -= Time.deltaTime;
        } else {
          InventoryGui.instance.m_animator.SetBool("visible", false);
          delayTime = 0;
        }
      }

      private static bool HasContainerAccess(Container container) {
        if (!container) {
          return false;
        }

        bool areaAccess = PrivateArea.CheckAccess(container.transform.position, 0f, false, false);
        bool chestAccess = container.CheckAccess(Game.m_instance.m_playerProfile.m_playerID);

        return areaAccess && chestAccess;
      }

      [HarmonyPatch(nameof(InventoryGui.SetupDragItem)), HarmonyPrefix]
      public static bool SetupDragItemPrefix() {
        return ShowRealGUI();
      }

      private static void ShowPreviewContainer(Inventory container) {
        InventoryGui.instance.m_animator.SetBool("visible", true);
        InventoryGui.instance.m_hiddenFrames = 10;
        InventoryGui.instance.m_container.gameObject.SetActive(true);

        // wait one frame to let the grid initialize properly
        if (!initializedGrids.Contains(InventoryGui.instance.m_containerGrid)) {
          return;
        }

        InventoryGui.instance.m_containerGrid.UpdateInventory(container, null, null);
        InventoryGui.instance.m_containerGrid.ResetView();
        InventoryGui.instance.m_containerName.text = Localization.instance.Localize(container.GetName());
        int containerWeight = Mathf.CeilToInt(container.GetTotalWeight());
        InventoryGui.instance.m_containerWeight.text = containerWeight.ToString();
      }
    }
  }

  public static class ObjectExtensions {
    public static T Ref<T>(this T o) where T : UnityEngine.Object {
      return o ? o : null;
    }
  }
}
