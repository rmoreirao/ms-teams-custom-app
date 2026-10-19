param prefix string
param location string
param busNamespaceId string
param logsWorkspaceId string
param actionGroupId string = ''

var actions = empty(actionGroupId) ? [] : [{ actionGroupId: actionGroupId }]
resource deadLetters 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-graph-deadletters'
  location: 'global'
  properties: {
    description: 'Graph notification or delivery dead letters require repair/retention investigation.'
    severity: 1
    enabled: true
    scopes: [busNamespaceId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{
        name: 'DeadLetters'
        metricNamespace: 'Microsoft.ServiceBus/namespaces'
        metricName: 'DeadletteredMessages'
        operator: 'GreaterThan'
        threshold: 0
        timeAggregation: 'Maximum'
        dimensions: [{ name: 'EntityName', operator: 'Include', values: ['graph-notifications', 'graph-delivery'] }]
        criterionType: 'StaticThresholdCriterion'
      }]
    }
    actions: actions
  }
}
resource backlog 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-graph-backlog'
  location: 'global'
  properties: {
    description: 'Sustained queue backlog; inspect processing age, dependency budgets and autoscaling before raising capacity.'
    severity: 2
    enabled: true
    scopes: [busNamespaceId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{
        name: 'Backlog'
        metricNamespace: 'Microsoft.ServiceBus/namespaces'
        metricName: 'ActiveMessages'
        operator: 'GreaterThan'
        threshold: 1000
        timeAggregation: 'Minimum'
        dimensions: [{ name: 'EntityName', operator: 'Include', values: ['graph-notifications', 'graph-delivery'] }]
        criterionType: 'StaticThresholdCriterion'
      }]
    }
    actions: actions
  }
}
resource failures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${prefix}-live-worker-failures'
  location: location
  properties: {
    displayName: 'Live Teams worker failures'
    description: 'Body-free application worker-failure counter; excludes normal shared-budget deferrals.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    scopes: [logsWorkspaceId]
    autoMitigate: true
    criteria: { allOf: [{
      query: 'AppMetrics | where Name == "teamsref_worker_failures" | summarize Failures=sum(Sum) by bin(TimeGenerated, 5m)'
      timeAggregation: 'Total'
      metricMeasureColumn: 'Failures'
      operator: 'GreaterThan'
      threshold: 0
      failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
    }] }
    actions: { actionGroups: empty(actionGroupId) ? [] : [actionGroupId] }
  }
}
resource workbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: guid(resourceGroup().id, prefix, 'live-operations')
  location: location
  kind: 'shared'
  properties: {
    displayName: '${prefix} live Teams operations'
    category: 'workbook'
    sourceId: logsWorkspaceId
    serializedData: string({
      version: 'Notebook/1.0'
      items: [
        {
          type: 1
          content: { json: '# Live Teams operations\nBody-free counters and dependency timings. Broker acceptance is not Graph confirmation or client rendering. Review subscription/coverage, policy leases, queue/DLQ age and operation uncertainty alongside these charts.' }
          name: 'context'
        }
        {
          type: 3
          name: 'throughput'
          content: {
            version: 'KqlItem/1.0'
            query: 'AppMetrics | where Name startswith "teamsref_" and Name != "teamsref_dependency_duration_ms" | summarize Count=sum(Sum) by Name, bin(TimeGenerated, 1m) | render timechart'
            visualization: 'timechart'
            queryType: 0
            resourceType: 'microsoft.operationalinsights/workspaces'
            crossComponentResources: [logsWorkspaceId]
            timeContext: { durationMs: 3600000 }
            size: 0
          }
        }
        {
          type: 3
          name: 'dependency-latency'
          content: {
            version: 'KqlItem/1.0'
            query: 'AppMetrics | where Name == "teamsref_dependency_duration_ms" | summarize AverageMilliseconds=sum(Sum)/sum(ItemCount) by bin(TimeGenerated, 1m) | render timechart'
            visualization: 'timechart'
            queryType: 0
            resourceType: 'microsoft.operationalinsights/workspaces'
            crossComponentResources: [logsWorkspaceId]
            timeContext: { durationMs: 3600000 }
            size: 0
          }
        }
      ]
      fallbackResourceIds: [logsWorkspaceId]
    })
  }
}
output workbookId string = workbook.id
