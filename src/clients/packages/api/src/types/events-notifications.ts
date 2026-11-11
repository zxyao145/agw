import type { components, paths } from "../openapi";

export type JsonValue = components["schemas"]["JsonElement"];

export type EventSourceResponse = components["schemas"]["EventSourceResponse"];
export type EventSourceCreateRequest = components["schemas"]["EventSourceCreateRequest"];
export type EventSourceUpdateRequest = components["schemas"]["EventSourceUpdateRequest"];
export type EventSourceVersionRequest = components["schemas"]["EventSourceVersionRequest"];
export type EventSourceEnableRequest = components["schemas"]["EventSourceEnableRequest"];
export type EventSourceMutationResponse = components["schemas"]["EventSourceMutationResponse"];
export type EventSourceTestResponse = components["schemas"]["EventSourceTestResponse"];

export type EventSubscriptionRequest = components["schemas"]["EventSubscriptionRequest"];
export type EventSubscriptionResponse = components["schemas"]["EventSubscriptionResponse"];

export type CloudEventResponse = components["schemas"]["CloudEventResponse"];
export type EventResponse = components["schemas"]["EventResponse"];
export type EventListQuery = NonNullable<paths["/api/events/list"]["get"]["parameters"]["query"]>;
export type EventDeliveryResponse = components["schemas"]["EventDeliveryResponse"];
export type EventDeliveryActionResponse = components["schemas"]["EventDeliveryActionResponse"];
export type EventScheduleResponse = components["schemas"]["EventScheduleResponse"];
export type UnpublishedEventRecord = components["schemas"]["UnpublishedEventRecord"];

export type NotificationKind = components["schemas"]["NotificationKind"];
export type NotificationState = components["schemas"]["NotificationState"];
export type NotificationOption = components["schemas"]["NotificationOption"];
export type NotificationListItem = components["schemas"]["NotificationListItem"];
export type NotificationQuestionResponse = components["schemas"]["NotificationQuestionResponse"];
export type NotificationDispatchAttemptResponse =
  components["schemas"]["NotificationDispatchAttemptResponse"];
export type NotificationDispatchResponse = components["schemas"]["NotificationDispatchResponse"];
export type NotificationDetail = components["schemas"]["NotificationDetail"];
export type NotificationAnswerRequest = components["schemas"]["NotificationAnswerRequest"];
export type NotificationAnswerResult = components["schemas"]["NotificationAnswerResult"];
export type NotificationReadRequest = components["schemas"]["NotificationReadRequest"];
export type NotificationDeliveredRequest = components["schemas"]["NotificationDeliveredRequest"];
export type NotificationDispatchRetryRequest =
  components["schemas"]["NotificationDispatchRetryRequest"];
export type NotificationChannelResponse = components["schemas"]["NotificationChannelResponse"];
