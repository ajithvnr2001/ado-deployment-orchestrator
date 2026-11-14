output "resource_group_name" {
  value = azurerm_resource_group.orchestrator.name
}

output "api_url" {
  description = "Orchestration Agent base URL (HTTPS via Container Apps ingress)."
  value       = "https://${azurerm_container_app.api.ingress.0.fqdn}"
}

output "frontend_url" {
  description = "Chat Deployment Controller static site URL."
  value       = "https://${azurerm_storage_account.frontend.primary_web_host}"
}

output "acr_login_server" {
  value = azurerm_container_registry.orchestrator.login_server
}

output "sql_server_name" {
  value = azurerm_mssql_server.orchestrator.name
}

output "key_vault_name" {
  value = azurerm_key_vault.orchestrator.name
}

output "signalr_name" {
  value = azurerm_signalr_service.orchestrator.name
}
