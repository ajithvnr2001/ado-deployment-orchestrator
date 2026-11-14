# Copy to secrets.tfvars (gitignored) or pass via -var:
#   terraform apply -var "sql_admin_password=$(openssl rand -hex 16)"
location          = "northeurope"
workload_location = "swedencentral"
environment       = "dev"
sql_admin_login   = "orchadmin"
deployer_ip       = "203.0.113.10"
