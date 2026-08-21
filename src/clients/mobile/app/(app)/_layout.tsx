import { Stack } from "expo-router";
import React from "react";

export default function AppLayout(): React.JSX.Element {
  return (
    <Stack screenOptions={{ headerShown: false }}>
      <Stack.Screen name="chat" />
      <Stack.Screen name="files" />
      <Stack.Screen
        name="history"
        options={{
          presentation: "card",
          animation: "slide_from_left",
          animationMatchesGesture: true,
          animationTypeForReplace: "pop",
          fullScreenGestureEnabled: true,
        }}
      />
      <Stack.Screen name="file-preview" options={{ presentation: "fullScreenModal" }} />
    </Stack>
  );
}
