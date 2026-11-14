terraform {
  required_version = ">= 1.9.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
  # Local state by default (single-click demo). For team use, uncomment:
  # backend "azurerm" {
  #   resource_group_name  = "rg-tfstate"
  #   storage_account_name = "<tfstatestorage>"
  #   container_name       = "tfstate"
  #   key                  = "orchestrator.tfstate"
  # }
}

provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy = true
    }
    resource_group {
      prevent_deletion_if_contains_resources = false
    }
  }
}

data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length  = 6
  lower   = true
  upper   = false
  special = false
}

locals {
  suffix       = random_string.suffix.result
  rg_name      = "rg-orchestrator-${local.suffix}"
  acr_name     = "acrorch${local.suffix}"
  sql_name     = "sql-orch-${local.suffix}"
  kv_name      = "kv-orch-${local.suffix}"
  signalr_name = "signalr-orch-${local.suffix}"
  storage_name = "stororch${local.suffix}"
  cae_name     = "cae-orch-${local.suffix}"
  law_name     = "law-orch-${local.suffix}"
  container_name = "ca-orch-api-${local.suffix}"
  tags = {
    project     = "ado-deployment-orchestrator"
    managed_by  = "terraform"
    environment = var.environment
  }
}

resource "azurerm_resource_group" "orchestrator" {
  name     = local.rg_name
  location = var.location
  tags     = local.tags
}

# ---------------------------------------------------------------------------
# Azure Container Apps (hosts the .NET 10 Orchestration Agent)
# ---------------------------------------------------------------------------
resource "azurerm_log_analytics_workspace" "orchestrator" {
  name                = local.law_name
  location            = var.workload_location
  resource_group_name = azurerm_resource_group.orchestrator.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_container_app_environment" "orchestrator" {
  name                       = local.cae_name
  location                   = var.workload_location
  resource_group_name        = azurerm_resource_group.orchestrator.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.orchestrator.id
}

resource "azurerm_container_registry" "orchestrator" {
  name                          = local.acr_name
  resource_group_name           = azurerm_resource_group.orchestrator.name
  location                      = var.workload_location
  sku                           = "Basic"
  admin_enabled                 = true
  public_network_access_enabled = true
}

resource "azurerm_container_app" "api" {
  name                         = local.container_name
  resource_group_name          = azurerm_resource_group.orchestrator.name
  container_app_environment_id = azurerm_container_app_environment.orchestrator.id
  revision_mode                = "Single"

  identity {
    type = "SystemAssigned"
  }

  registry {
    server               = azurerm_container_registry.orchestrator.login_server
    username             = azurerm_container_registry.orchestrator.admin_username
    password_secret_name = "acr-password"
  }

  secret {
    name  = "sql-connection-string"
    value = local.sql_connection_string
  }

  secret {
    name  = "signalr-connection-string"
    value = azurerm_signalr_service.orchestrator.primary_connection_string
  }

  secret {
    name  = "acr-password"
    value = azurerm_container_registry.orchestrator.admin_password
  }

  template {
    min_replicas = 1
    max_replicas = 2

    container {
      name   = "orchestrator-api"
      image  = var.initial_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__Sql"
        secret_name = "sql-connection-string"
      }
      env {
        name        = "Azure__SignalR__ConnectionString"
        secret_name = "signalr-connection-string"
      }
      env {
        name  = "Cors__Origins__0"
        value = "https://${azurerm_storage_account.frontend.primary_web_host}"
      }
      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "Ado__Organization"
        value = ""
      }
      env {
        name  = "Monitoring__IntervalSeconds"
        value = "15"
      }
      env {
        name  = "Monitoring__PollTimeoutMinutes"
        value = "40"
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  depends_on = [azurerm_signalr_service.orchestrator]
}

# ---------------------------------------------------------------------------
# Static frontend hosting (Azure Storage static website)
# ---------------------------------------------------------------------------
resource "azurerm_storage_account" "frontend" {
  name                     = local.storage_name
  resource_group_name      = azurerm_resource_group.orchestrator.name
  location                 = azurerm_resource_group.orchestrator.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"

  static_website {
    index_document     = "index.html"
    error_404_document = "index.html"
  }

  tags = local.tags
}

# ---------------------------------------------------------------------------
# Azure SignalR (real-time monitoring push over websockets)
# ---------------------------------------------------------------------------
resource "azurerm_signalr_service" "orchestrator" {
  name                = local.signalr_name
  location            = azurerm_resource_group.orchestrator.location
  resource_group_name = azurerm_resource_group.orchestrator.name
  service_mode        = "Default"

  sku {
    name     = "Free_F1"
    capacity = 1
  }
}

# ---------------------------------------------------------------------------
# Azure SQL (complete audit & history)
# ---------------------------------------------------------------------------
resource "azurerm_mssql_server" "orchestrator" {
  name                         = local.sql_name
  resource_group_name          = azurerm_resource_group.orchestrator.name
  location                     = var.workload_location
  version                      = "12.0"
  administrator_login          = var.sql_admin_login
  administrator_login_password = var.sql_admin_password
  minimum_tls_version          = "1.2"

  azuread_administrator {
    login_username = "sql-aad-admin"
    object_id      = data.azurerm_client_config.current.object_id
    tenant_id      = data.azurerm_client_config.current.tenant_id
  }
  tags = local.tags
}

resource "azurerm_mssql_firewall_rule" "allow_azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.orchestrator.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_mssql_firewall_rule" "allow_deployer" {
  count            = var.deployer_ip != "" ? 1 : 0
  name             = "AllowDeployerIP"
  server_id        = azurerm_mssql_server.orchestrator.id
  start_ip_address = var.deployer_ip
  end_ip_address   = var.deployer_ip
}

resource "azurerm_mssql_database" "orchestrator" {
  name      = "orchestrator-db"
  server_id = azurerm_mssql_server.orchestrator.id
  sku_name  = "S0"
  collation = "SQL_Latin1_General_CP1_CI_AS"
  tags      = local.tags
}

# ---------------------------------------------------------------------------
# Key Vault (secrets: SQL connection string for safe-keeping)
# ---------------------------------------------------------------------------
resource "azurerm_key_vault" "orchestrator" {
  name                       = local.kv_name
  location                   = var.workload_location
  resource_group_name        = azurerm_resource_group.orchestrator.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  soft_delete_retention_days = 7

  access_policy {
    tenant_id          = data.azurerm_client_config.current.tenant_id
    object_id          = data.azurerm_client_config.current.object_id
    key_permissions    = ["Get", "List"]
    secret_permissions = ["Get", "List", "Set", "Delete"]
  }

  tags = local.tags
}

locals {
  sql_connection_string = "Server=tcp:${azurerm_mssql_server.orchestrator.fully_qualified_domain_name},1433;Database=${azurerm_mssql_database.orchestrator.name};User ID=${var.sql_admin_login};Password=${var.sql_admin_password};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
}

resource "azurerm_key_vault_secret" "sql_connection" {
  name         = "sql-connection-string"
  value        = local.sql_connection_string
  key_vault_id = azurerm_key_vault.orchestrator.id
}

# Grant the container app's managed identity Key Vault read access (RBAC path)
resource "azurerm_role_assignment" "api_kv_secrets_user" {
  scope                = azurerm_key_vault.orchestrator.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_container_app.api.identity[0].principal_id
}
