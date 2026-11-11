export type DesktopNotification = {
  id: string;
  createdAt: string;
  kind: number | string;
  state: number | string;
  title: string;
  text: string;
  link: string | null;
  readAt: string | null;
  changedAt: string;
  version: number;
};

export type DesktopNotificationFrame = {
  serverId: string;
  notification: DesktopNotification;
};
