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
    description: 'Graph notification, delivery or read-refresh dead letters require recovery/retention investigation.'
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
        dimensions: [{ name: 'EntityName', operator: 'Include', values: ['graph-notifications', 'graph-delivery', 'graph-read-refresh'] }]
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
        dimensions: [{ name: 'EntityName', operator: 'Include', values: ['graph-notifications', 'graph-delivery', 'graph-read-refresh'] }]
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
    description: 'Body-free application worker-failure counter; excludes isolated Graph throttle recovery.'
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
resource refreshFailures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${prefix}-read-refresh-stalled'
  location: location
  properties: {
    displayName: 'Sustained Teams read refresh failures or aged work'
    description: 'Work age, not last source message time. Isolated Graph throttling is not a failure. Verify action-group routing separately.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    scopes: [logsWorkspaceId]
    autoMitigate: true
    criteria: { allOf: [{
      query: 'AppMetrics | where Name in ("teamsref_read_refresh_failures", "teamsref_read_refresh_age_ms", "teamsref_repair_stalled") | summarize Failures=sumif(Sum, Name == "teamsref_read_refresh_failures"), Oldest=maxif(Max, Name == "teamsref_read_refresh_age_ms"), Stalled=maxif(Max, Name == "teamsref_repair_stalled") by bin(TimeGenerated, 1m) | where Failures >= 3 or Oldest >= 120000 or Stalled > 0 | summarize UnhealthyMinutes=count()'
      timeAggregation: 'Maximum'
      metricMeasureColumn: 'UnhealthyMinutes'
      operator: 'GreaterThanOrEqual'
      threshold: 2
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
          content: { json: '# Live Teams operations\nBody-free counters and dependency timings. Broker acceptance is not Graph confirmation or client rendering. Review subscription/repair progress, policy leases, read-refresh work age, all three queues/DLQs and operation uncertainty. Retained authorized content is not proof of source freshness. An empty action group does not route support alerts.' }
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
