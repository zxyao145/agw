import React from "react";
import { ScrollView, Text, View } from "react-native";
import { styles } from "./styles";

export function ChatPanel(): React.JSX.Element {
  return (
    <ScrollView
      alwaysBounceVertical={false}
      contentContainerStyle={styles.chatContent}
      style={styles.panelScroll}
    >
      <View style={styles.dateDivider}>
        <Text style={styles.dateDividerText}>TODAY, OCT 24</Text>
      </View>

      <View style={styles.selfMessageRow}>
        <View style={[styles.selfBubble, styles.bubbleShadow]}>
          <Text style={styles.selfBubbleText}>
            Just finished reviewing them. I've highlighted the growth in the
            tech sector segments. Attaching the summary now.
          </Text>
        </View>
      </View>

      <View style={styles.receiverGroup}>
        <Text style={styles.senderLabel}>Sarah Miller - 10:24 AM</Text>
        <View style={[styles.receiverBubble, styles.bubbleShadow]}>
          <Text style={styles.receiverBubbleText}>
            Did you have a chance to review the Q4 financial projections? The
            board is meeting at 2:00 PM.
          </Text>
        </View>
      </View>

      <View style={styles.receiverGroupCompact}>
        <View
          style={[
            styles.receiverBubble,
            styles.receiverBubbleCompact,
            styles.bubbleShadow,
          ]}
        >
          <Text style={styles.receiverBubbleText}>
            Excellent. Could you also bring the hard copies to Room 402?
          </Text>
        </View>
      </View>

      <View style={styles.typingIndicator}>
        <Text style={styles.typingText}>Sarah is typing</Text>
        <View style={styles.typingDots}>
          <View style={styles.typingDot} />
          <View style={styles.typingDot} />
          <View style={styles.typingDot} />
        </View>
      </View>
    </ScrollView>
  );
}
