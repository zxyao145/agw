// Loaded explicitly by Agw; no host provider or credential files are changed.
export default function (pi) {
  const configuration = JSON.parse(process.env.AGW_PI_MODEL_CONFIGURATION);
  pi.registerProvider(configuration.providerId, {
    baseUrl: configuration.baseUrl,
    api: configuration.api,
    apiKey: "$AGW_EXTERNAL_MODEL_API_KEY",
    models: [configuration.model],
  });
}
