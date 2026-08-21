export const colors = {
  background: "#FAF9FE",
  surface: "#F4F3F8",
  segment: "#EEEDF3",
  border: "#C1C6D7",
  primary: "#0058BC",
  primaryBright: "#0070EB",
  primarySoft: "#D7E8FF",
  receiver: "#E3E2E7",
  datePill: "#E9E7ED",
  ink: "#1A1B1F",
  muted: "#414755",
  subtle: "#727783",
  white: "#FFFFFF",
  black: "#000000",
  danger: "#BA1A1A",
  dangerSoft: "#FFDAD6",
  warning: "#7A5900",
  warningSoft: "#FFF1C2",
  success: "#176B3A",
  successSoft: "#D5F6DF",
  overlay: "rgba(26, 27, 31, 0.32)",
  code: "#ECEBF1",
} as const;

export const spacing = {
  xxs: 4,
  xs: 8,
  sm: 12,
  md: 16,
  lg: 20,
  xl: 24,
} as const;

export const radius = {
  sm: 8,
  md: 12,
  lg: 16,
  pill: 999,
} as const;

export const typography = {
  regular: "Inter_400Regular",
  medium: "Inter_500Medium",
  semibold: "Inter_600SemiBold",
} as const;

export const layout = {
  headerHeight: 64,
  maxPhoneWidth: 480,
  minHitTarget: 44,
} as const;
