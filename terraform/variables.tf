variable "location" {
  description = "Azure region for resource group, SignalR and the static frontend."
  type        = string
  default     = "northeurope"
}

variable "workload_location" {
  description = "Azure region for the compute + data plane (Container Apps, ACR, SQL, Key Vault)."
  type        = string
  default     = "swedencentral"
}

variable "environment" {
  description = "Environment tag (dev/qa/prod)."
  type        = string
  default     = "dev"
}

variable "initial_image" {
  description = "Placeholder image for the container app until deploy.sh pushes the real one."
  type        = string
  default     = "mcr.microsoft.com/dotnet/aspnet:10.0"
}

variable "sql_admin_login" {
  description = "Azure SQL administrator login."
  type        = string
  default     = "orchadmin"
}

variable "sql_admin_password" {
  description = "Azure SQL administrator password (sensitive)."
  type        = string
  sensitive   = true
}

variable "deployer_ip" {
  description = "Deployer public IP allowed to reach Azure SQL (empty = skip rule)."
  type        = string
  default     = ""
}
