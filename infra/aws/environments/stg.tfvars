aws_region      = "us-east-1"
environment     = "stg"
vpc_cidr        = "10.40.0.0/16"
enable_services = false
enable_cache    = false

# After AWS provisions your dedicated short codes, provide each approved ARN:
# sms_origination_identities = {
#   US = "arn:aws:sms-voice:REGION:ACCOUNT_ID:phone-number/phone-US_ID"
#   BG = "arn:aws:sms-voice:REGION:ACCOUNT_ID:phone-number/phone-BG_ID"
# }
