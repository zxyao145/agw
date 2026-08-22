export type NativeChatTheme = {
  background: string;
  surface: string;
  ink: string;
  muted: string;
  primary: string;
  border: string;
  code: string;
  danger: string;
  dangerSoft: string;
  warning: string;
  warningSoft: string;
  white: string;
  fontRegular: string;
  fontMedium: string;
  fontSemibold: string;
};

export const defaultNativeChatTheme: NativeChatTheme = {
  background: "#ffffff",
  surface: "#faf9fe",
  ink: "#17191d",
  muted: "#727680",
  primary: "#075fca",
  border: "#c8ccda",
  code: "#f3f3f4",
  danger: "#b42318",
  dangerSoft: "#fef3f2",
  warning: "#b54708",
  warningSoft: "#fffaeb",
  white: "#ffffff",
  fontRegular: "Inter_400Regular",
  fontMedium: "Inter_500Medium",
  fontSemibold: "Inter_600SemiBold",
};
