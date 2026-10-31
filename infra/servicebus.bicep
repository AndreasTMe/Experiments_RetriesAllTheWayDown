param location string
param namespaceName string
param maxDeliveryCount int

resource namespace 'Microsoft.ServiceBus/namespaces@2026-01-01' = {
  name: namespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    // The AppHost and the Runner connect to the emulator with a connection string.
    disableLocalAuth: false
  }
}

// Every value matches the emulator. The first five are set in the AppHost, the others were read back from
// emulator 2.0.0 at runtime.
resource orders 'Microsoft.ServiceBus/namespaces/queues@2026-01-01' = {
  parent: namespace
  name: 'orders'
  properties: {
    maxDeliveryCount: maxDeliveryCount
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: false
    requiresSession: false
    requiresDuplicateDetection: false
    defaultMessageTimeToLive: 'PT1H'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enableBatchedOperations: true
    // TimeSpan.MaxValue, which Service Bus treats as never.
    autoDeleteOnIdle: 'P10675199DT2H48M5.4775807S'
  }
}

output namespaceName string = namespace.name
