import React from "react";
import { Pressable, StyleProp, Text, View, ViewStyle } from "react-native";
import { styles } from "./styles";
import { colors } from "./tokens";

export type IconName =
  | "menu"
  | "plus"
  | "more"
  | "close"
  | "chevronDown"
  | "chevronLeft"
  | "chevronRight"
  | "folder"
  | "info"
  | "image"
  | "paperclip"
  | "smile"
  | "mic"
  | "circlePlus"
  | "send"
  | "fileImage"
  | "filePdf"
  | "fileSheet"
  | "settings";

export function IconButton({
  color = colors.icon,
  icon,
  label,
  onPress,
  size = 40,
  style,
  testID,
}: {
  color?: string;
  icon: IconName;
  label: string;
  onPress?: () => void;
  size?: number;
  style?: StyleProp<ViewStyle>;
  testID?: string;
}): React.JSX.Element {
  return (
    <Pressable
      accessibilityLabel={label}
      accessibilityRole="button"
      onPress={onPress}
      style={({ pressed }) => [
        styles.iconButton,
        { height: size, opacity: pressed ? 0.65 : 1, width: size },
        style,
      ]}
      testID={testID}
    >
      <Icon color={color} name={icon} size={Math.min(24, size - 10)} />
    </Pressable>
  );
}

export function Icon({
  color = colors.icon,
  name,
  size = 24,
}: {
  color?: string;
  name: IconName;
  size?: number;
}): React.JSX.Element {
  const lineThickness = Math.max(2, Math.round(size / 12));

  if (name === "menu") {
    return (
      <View style={[styles.iconCanvas, { height: size, width: size }]}>
        {[0.28, 0.5, 0.72].map((position) => (
          <View
            key={position}
            style={[
              styles.iconLine,
              {
                backgroundColor: color,
                height: lineThickness,
                left: size * 0.12,
                top: size * position,
                width: size * 0.76,
              },
            ]}
          />
        ))}
      </View>
    );
  }

  if (name === "plus" || name === "close") {
    const rotation = name === "close" ? "45deg" : "0deg";
    const longSide = size * 0.72;

    return (
      <View
        style={[
          styles.iconCanvas,
          { height: size, transform: [{ rotate: rotation }], width: size },
        ]}
      >
        <View
          style={[
            styles.iconLine,
            {
              backgroundColor: color,
              height: lineThickness,
              left: (size - longSide) / 2,
              top: (size - lineThickness) / 2,
              width: longSide,
            },
          ]}
        />
        <View
          style={[
            styles.iconLine,
            {
              backgroundColor: color,
              height: longSide,
              left: (size - lineThickness) / 2,
              top: (size - longSide) / 2,
              width: lineThickness,
            },
          ]}
        />
      </View>
    );
  }

  if (name === "more") {
    return (
      <View style={[styles.iconCanvas, { height: size, width: size }]}>
        {[0.28, 0.5, 0.72].map((position) => (
          <View
            key={position}
            style={[
              styles.moreDot,
              {
                backgroundColor: color,
                height: size * 0.13,
                left: size * 0.44,
                top: size * position,
                width: size * 0.13,
              },
            ]}
          />
        ))}
      </View>
    );
  }

  if (
    name === "chevronDown" ||
    name === "chevronLeft" ||
    name === "chevronRight"
  ) {
    return (
      <View style={[styles.iconCenter, { height: size, width: size }]}>
        <View
          style={{
            borderBottomColor: color,
            borderBottomWidth: lineThickness,
            borderRightColor: color,
            borderRightWidth: lineThickness,
            height: size * 0.42,
            transform: [
              {
                rotate:
                  name === "chevronDown"
                    ? "45deg"
                    : name === "chevronLeft"
                      ? "135deg"
                      : "-45deg",
              },
            ],
            width: size * 0.42,
          }}
        />
      </View>
    );
  }

  if (name === "folder") {
    return (
      <View style={[styles.iconCanvas, { height: size, width: size }]}>
        <View
          style={[
            styles.folderTab,
            {
              backgroundColor: color,
              height: size * 0.24,
              left: size * 0.06,
              top: size * 0.18,
              width: size * 0.43,
            },
          ]}
        />
        <View
          style={[
            styles.folderBody,
            {
              backgroundColor: color,
              height: size * 0.56,
              top: size * 0.34,
              width: size,
            },
          ]}
        />
      </View>
    );
  }

  if (name === "info") {
    return (
      <View
        style={[
          styles.infoIcon,
          { borderColor: color, height: size, width: size },
        ]}
      >
        <Text style={[styles.infoIconText, { color, fontSize: size * 0.68 }]}>
          i
        </Text>
      </View>
    );
  }

  if (name === "image") {
    return (
      <View
        style={[
          styles.toolbarImageIcon,
          { borderColor: color, height: size, width: size },
        ]}
      >
        <View style={[styles.toolbarImageSun, { backgroundColor: color }]} />
        <View
          style={[
            styles.toolbarImageMountain,
            {
              borderBottomColor: color,
              borderLeftWidth: size * 0.18,
              borderRightWidth: size * 0.18,
              borderTopWidth: 0,
            },
          ]}
        />
      </View>
    );
  }

  if (name === "paperclip") {
    return (
      <View style={[styles.iconCenter, { height: size, width: size }]}>
        <View
          style={[
            styles.paperclipOuter,
            {
              borderColor: color,
              height: size * 0.82,
              width: size * 0.44,
            },
          ]}
        >
          <View style={[styles.paperclipInner, { borderColor: color }]} />
        </View>
      </View>
    );
  }

  if (name === "smile") {
    return (
      <View
        style={[
          styles.smileFace,
          { borderColor: color, height: size, width: size },
        ]}
      >
        <View
          style={[
            styles.smileEye,
            { backgroundColor: color, left: size * 0.31 },
          ]}
        />
        <View
          style={[
            styles.smileEye,
            { backgroundColor: color, right: size * 0.31 },
          ]}
        />
        <View style={[styles.smileMouth, { borderBottomColor: color }]} />
      </View>
    );
  }

  if (name === "mic") {
    return (
      <View style={[styles.iconCanvas, { height: size, width: size }]}>
        <View
          style={[
            styles.micCapsule,
            {
              borderColor: color,
              height: size * 0.58,
              left: size * 0.35,
              top: size * 0.1,
              width: size * 0.3,
            },
          ]}
        />
        <View
          style={[
            styles.micStem,
            {
              backgroundColor: color,
              height: size * 0.24,
              left: size * 0.48,
              top: size * 0.66,
              width: lineThickness,
            },
          ]}
        />
        <View
          style={[
            styles.micBase,
            {
              backgroundColor: color,
              height: lineThickness,
              left: size * 0.32,
              top: size * 0.88,
              width: size * 0.36,
            },
          ]}
        />
      </View>
    );
  }

  if (name === "circlePlus") {
    const longSide = size * 0.46;

    return (
      <View
        style={[
          styles.circlePlus,
          { borderColor: color, height: size, width: size },
        ]}
      >
        <View
          style={[
            styles.iconLine,
            {
              backgroundColor: color,
              height: lineThickness,
              left: (size - longSide) / 2,
              top: (size - lineThickness) / 2,
              width: longSide,
            },
          ]}
        />
        <View
          style={[
            styles.iconLine,
            {
              backgroundColor: color,
              height: longSide,
              left: (size - lineThickness) / 2,
              top: (size - longSide) / 2,
              width: lineThickness,
            },
          ]}
        />
      </View>
    );
  }

  if (name === "send") {
    return (
      <View style={[styles.iconCenter, { height: size, width: size }]}>
        <View
          style={{
            borderBottomColor: "transparent",
            borderBottomWidth: size * 0.3,
            borderLeftColor: color,
            borderLeftWidth: size * 0.78,
            borderTopColor: "transparent",
            borderTopWidth: size * 0.3,
            height: 0,
            width: 0,
          }}
        />
      </View>
    );
  }

  if (name === "fileImage") {
    return (
      <View
        style={[
          styles.fileImageIcon,
          { height: size * 0.8, width: size * 0.8 },
        ]}
      >
        <View style={styles.fileImageDot} />
        <View style={styles.fileImageHill} />
      </View>
    );
  }

  if (name === "filePdf") {
    return (
      <View style={[styles.filePdfIcon, { height: size, width: size * 0.78 }]}>
        <View style={styles.fileFold} />
        <View style={styles.filePdfLine} />
        <View style={[styles.filePdfLine, styles.filePdfLineShort]} />
      </View>
    );
  }

  if (name === "fileSheet") {
    return (
      <View
        style={[styles.fileSheetIcon, { height: size * 0.82, width: size }]}
      >
        <View style={styles.fileSheetHeader} />
        <View style={styles.sheetCells}>
          <View style={styles.sheetCell} />
          <View style={styles.sheetCell} />
          <View style={styles.sheetCell} />
        </View>
      </View>
    );
  }

  return (
    <View style={[styles.settingsIcon, { height: size, width: size }]}>
      <View style={[styles.settingsRing, { borderColor: color }]} />
      <View style={[styles.settingsCore, { backgroundColor: color }]} />
      <View
        style={[styles.settingsSpokeHorizontal, { backgroundColor: color }]}
      />
      <View
        style={[styles.settingsSpokeVertical, { backgroundColor: color }]}
      />
    </View>
  );
}
