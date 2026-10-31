// A resource group with the Standard namespace and the "orders" queue the cloud runs use.
targetScope = 'subscription'

param resourceGroupName string = 'rg-retries-all-the-way-down'
param location string = deployment().location

// Namespace names are global across Azure, so the default is unique to the subscription.
@minLength(6)
@maxLength(50)
param namespaceName string = 'retries-${uniqueString(subscription().id)}'

// The AppHost's Experiment:MaxDeliveryCount. Configuration 2b sets it to 1.
@minValue(1)
param maxDeliveryCount int = 10

resource resourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
}

module serviceBus 'servicebus.bicep' = {
  name: 'servicebus'
  scope: resourceGroup
  params: {
    location: location
    namespaceName: namespaceName
    maxDeliveryCount: maxDeliveryCount
  }
}

output resourceGroupName string = resourceGroup.name
output namespaceName string = serviceBus.outputs.namespaceName
