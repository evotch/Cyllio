variable "aws_region" {
  type    = string
  default = "us-east-1"
}
variable "environment" {
  type = string
  validation {
    condition     = contains(["stg", "prd"], var.environment)
    error_message = "Use stg or prd, with separate state for each environment."
  }
}
variable "project_name" {
  type    = string
  default = "cyllio"
  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,15}$", var.project_name))
    error_message = "Project name must be 2–16 lowercase letters, digits or hyphens, starting with a letter."
  }
}
variable "vpc_cidr" {
  type    = string
  default = "10.40.0.0/16"
}
variable "enable_services" {
  description = "Start application containers after images and account authentication secrets are available."
  type        = bool
  default     = false
}
variable "image_tag" {
  description = "Immutable image tag, normally a Git commit SHA."
  type        = string
  default     = ""
}
variable "account_secret_arn" {
  description = "Existing Secrets Manager JSON secret for account authentication; values never enter Terraform. Keys: authSigningKey, authPhoneHashKey."
  type        = string
  default     = null
  nullable    = true
  validation {
    condition     = var.account_secret_arn == null ? true : can(regex("^arn:[^:]+:secretsmanager:[^:]+:[0-9]{12}:secret:.+$", var.account_secret_arn))
    error_message = "Supply an existing Secrets Manager secret ARN or null."
  }
}
variable "account_secret_kms_key_arn" {
  description = "Customer-managed KMS key ARN for the account authentication secret, if applicable."
  type        = string
  default     = null
}
variable "enable_cache" {
  type    = bool
  default = false
}
variable "task_cpu" {
  type    = number
  default = 256
  validation {
    condition     = contains([256, 512, 1024, 2048, 4096], var.task_cpu)
    error_message = "Select a supported Fargate CPU size."
  }
}
variable "task_memory" {
  type    = number
  default = 512
}
variable "log_retention_days" {
  type    = number
  default = 30
}
variable "alarm_topic_arn" {
  description = "Optional existing SNS topic for operational alarm notifications."
  type        = string
  default     = null
}
