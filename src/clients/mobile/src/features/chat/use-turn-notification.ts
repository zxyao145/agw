import type { TurnFinishedStatus } from "@agw/execution-core";
import * as Notifications from "expo-notifications";
import React from "react";
import { AppState, Platform } from "react-native";

import { getTurnNotificationContent, toTurnNotifyStatus } from "./turn-notification";

export function useTurnNotification(): (status: TurnFinishedStatus) => void {
  React.useEffect(() => {
    Notifications.setNotificationHandler({
      handleNotification: async () => ({
        shouldShowBanner: false,
        shouldShowList: false,
        shouldPlaySound: false,
        shouldSetBadge: false,
      }),
    });
    // Android 13+ 要求先创建通道再进入权限流程；权限在应用首次打开时申请。
    void (async () => {
      try {
        if (Platform.OS === "android") {
          await Notifications.setNotificationChannelAsync("default", {
            name: "Default",
            importance: Notifications.AndroidImportance.HIGH,
          });
        }
        await Notifications.requestPermissionsAsync();
      } catch {
        // 通知不可用时不阻塞应用启动。
      }
    })();
  }, []);

  return React.useCallback((status: TurnFinishedStatus) => {
    const notify = toTurnNotifyStatus(status);
    if (!notify) return;
    if (AppState.currentState === "active") return;
    void (async () => {
      try {
        const settings = await Notifications.getPermissionsAsync();
        const provisional =
          settings.ios?.status === Notifications.IosAuthorizationStatus.PROVISIONAL;
        if (!settings.granted && !provisional) return;
        await Notifications.scheduleNotificationAsync({
          content: getTurnNotificationContent(notify),
          trigger: null,
        });
      } catch {
        // 调度失败时静默放弃；通知是尽力而为的补充。
      }
    })();
  }, []);
}
