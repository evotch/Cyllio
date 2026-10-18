terraform {
  required_version = ">= 1.10, < 2.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
  backend "s3" {}
}
provider "aws" {
  region = var.aws_region
  default_tags {
    tags = { Project = var.project_name, Environment = var.environment, ManagedBy = "Terraform" }
  }
}
data "aws_caller_identity" "current" {}
data "aws_partition" "current" {}
data "aws_availability_zones" "available" { state = "available" }
locals {
  prefix           = "${var.project_name}-${var.environment}"
  manifest         = jsondecode(file("${path.module}/../../services.json"))
  services         = { for service in local.manifest : service.name => service }
  deployed         = var.enable_services ? local.services : {}
  azs              = slice(data.aws_availability_zones.available.names, 0, 2)
  account_services = toset(["identity", "device"])
  consumers        = toset(["notification", "realtime", "trust-safety"])
  namespace        = "${local.prefix}.internal"
  account_secrets = var.account_secret_arn == null ? [] : [
    { name = "Mongo__ConnectionString", valueFrom = "${var.account_secret_arn}:mongoConnectionString::" },
    { name = "Auth__SigningKey", valueFrom = "${var.account_secret_arn}:authSigningKey::" },
    { name = "Auth__PhoneHashKey", valueFrom = "${var.account_secret_arn}:authPhoneHashKey::" }
  ]
}
