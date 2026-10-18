variable "enable_sms" {
  description = "Provision AWS End User Messaging SMS configuration and delivery logging. Approved country-specific short codes are provided separately."
  type        = bool
  default     = true
}
variable "sms_origination_identities" {
  description = "Approved short-code phone-number (or phone-pool) ARNs keyed by destination country US/BG. No number is reserved or registered automatically."
  type        = map(string)
  default     = {}
  validation {
    condition = alltrue([for country, arn in var.sms_origination_identities :
      contains(["US", "BG"], country) && can(regex("^arn:[^:]+:sms-voice:[^:]+:[0-9]{12}:(phone-number|pool)/[A-Za-z0-9-]+$", arn))
    ])
    error_message = "Use US/BG keys and approved AWS SMS phone-number/pool ARNs, not a five-digit number or arbitrary sender string."
  }
}
resource "aws_pinpointsmsvoicev2_configuration_set" "otp" {
  count                = var.enable_sms ? 1 : 0
  name                 = "${local.prefix}-otp"
  default_message_type = "TRANSACTIONAL"
  tags                 = merge(local.common_tags, { Service = "identity" })
}
resource "aws_cloudwatch_log_group" "sms" {
  count             = var.enable_sms ? 1 : 0
  name              = "/sms/${local.prefix}/delivery"
  retention_in_days = var.log_retention_days
  tags              = merge(local.common_tags, { Service = "identity" })
}
resource "aws_iam_role" "sms_events" {
  count = var.enable_sms ? 1 : 0
  name  = "${local.prefix}-sms-events"
  tags  = merge(local.common_tags, { Service = "identity" })
  assume_role_policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Effect = "Allow", Principal = { Service = "sms-voice.amazonaws.com" }, Action = "sts:AssumeRole",
    Condition = {
      StringEquals = { "aws:SourceAccount" = data.aws_caller_identity.current.account_id },
      ArnEquals    = { "aws:SourceArn" = aws_pinpointsmsvoicev2_configuration_set.otp[0].arn }
    }
  }] })
}
resource "aws_iam_role_policy" "sms_events" {
  count = var.enable_sms ? 1 : 0
  role  = aws_iam_role.sms_events[0].id
  policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Effect   = "Allow", Action = ["logs:CreateLogStream", "logs:PutLogEvents"],
    Resource = "${aws_cloudwatch_log_group.sms[0].arn}:*"
  }] })
}
resource "aws_pinpointsmsvoicev2_event_destination" "otp" {
  count                  = var.enable_sms ? 1 : 0
  configuration_set_name = aws_pinpointsmsvoicev2_configuration_set.otp[0].name
  event_destination_name = "delivery"
  enabled                = true
  matching_event_types   = ["TEXT_ALL"]
  cloudwatch_logs_destination {
    iam_role_arn  = aws_iam_role.sms_events[0].arn
    log_group_arn = aws_cloudwatch_log_group.sms[0].arn
  }
  depends_on = [aws_iam_role_policy.sms_events]
}
resource "aws_iam_role_policy" "identity_sms" {
  count = var.enable_sms && length(var.sms_origination_identities) > 0 ? 1 : 0
  role  = aws_iam_role.task["identity"].id
  policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Effect    = "Allow", Action = ["sms-voice:SendTextMessage"],
    Resource  = concat(distinct(values(var.sms_origination_identities)), [aws_pinpointsmsvoicev2_configuration_set.otp[0].arn]),
    Condition = { StringEquals = { "aws:RequestedRegion" = var.aws_region } }
  }] })
}
output "sms_configuration_set" { value = var.enable_sms ? aws_pinpointsmsvoicev2_configuration_set.otp[0].name : null }
output "sms_delivery_log_group" { value = var.enable_sms ? aws_cloudwatch_log_group.sms[0].name : null }
output "sms_origination_identities" { value = var.sms_origination_identities }
