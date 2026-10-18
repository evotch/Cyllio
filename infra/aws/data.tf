resource "aws_kms_key" "data" {
  description             = "${local.prefix} server-side data encryption; not client E2EE keys"
  enable_key_rotation     = true
  deletion_window_in_days = 30
}
resource "aws_kms_alias" "data" {
  name          = "alias/${local.prefix}-data"
  target_key_id = aws_kms_key.data.key_id
}
resource "aws_dynamodb_table" "main" {
  name                        = "${local.prefix}-entities"
  billing_mode                = "PAY_PER_REQUEST"
  hash_key                    = "PK"
  range_key                   = "SK"
  deletion_protection_enabled = var.environment == "prd"
  stream_enabled              = true
  stream_view_type            = "NEW_AND_OLD_IMAGES"
  attribute {
    name = "PK"
    type = "S"
  }
  attribute {
    name = "SK"
    type = "S"
  }
  attribute {
    name = "GSI1PK"
    type = "S"
  }
  attribute {
    name = "GSI1SK"
    type = "S"
  }
  global_secondary_index {
    name            = "GSI1"
    hash_key        = "GSI1PK"
    range_key       = "GSI1SK"
    projection_type = "ALL"
  }
  ttl {
    attribute_name = "expiresAt"
    enabled        = true
  }
  server_side_encryption {
    enabled     = true
    kms_key_arn = aws_kms_key.data.arn
  }
  point_in_time_recovery { enabled = true }
}
resource "aws_s3_bucket" "media" {
  bucket        = "${local.prefix}-media-${data.aws_caller_identity.current.account_id}-${var.aws_region}"
  force_destroy = false
}
resource "aws_s3_bucket_public_access_block" "media" {
  bucket                  = aws_s3_bucket.media.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}
resource "aws_s3_bucket_ownership_controls" "media" {
  bucket = aws_s3_bucket.media.id
  rule { object_ownership = "BucketOwnerEnforced" }
}
resource "aws_s3_bucket_versioning" "media" {
  bucket = aws_s3_bucket.media.id
  versioning_configuration { status = "Enabled" }
}
resource "aws_s3_bucket_server_side_encryption_configuration" "media" {
  bucket = aws_s3_bucket.media.id
  rule {
    bucket_key_enabled = true
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = aws_kms_key.data.arn
    }
  }
}
resource "aws_s3_bucket_policy" "media" {
  bucket = aws_s3_bucket.media.id
  policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Sid       = "DenyInsecureTransport", Effect = "Deny", Principal = "*", Action = "s3:*",
    Resource  = [aws_s3_bucket.media.arn, "${aws_s3_bucket.media.arn}/*"],
    Condition = { Bool = { "aws:SecureTransport" = "false" } }
  }] })
}
resource "aws_s3_bucket_lifecycle_configuration" "media" {
  bucket = aws_s3_bucket.media.id
  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"
    filter {}
    abort_incomplete_multipart_upload { days_after_initiation = 1 }
  }
}
resource "aws_sns_topic" "events" {
  name              = "${local.prefix}-events"
  kms_master_key_id = "alias/aws/sns"
}
resource "aws_sqs_queue" "dead_letter" {
  for_each                  = local.consumers
  name                      = "${local.prefix}-${each.key}-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}
resource "aws_sqs_queue" "consumer" {
  for_each                   = local.consumers
  name                       = "${local.prefix}-${each.key}"
  message_retention_seconds  = 345600
  visibility_timeout_seconds = 60
  receive_wait_time_seconds  = 20
  sqs_managed_sse_enabled    = true
  redrive_policy             = jsonencode({ deadLetterTargetArn = aws_sqs_queue.dead_letter[each.key].arn, maxReceiveCount = 5 })
}
resource "aws_sqs_queue_redrive_allow_policy" "consumer" {
  for_each             = local.consumers
  queue_url            = aws_sqs_queue.dead_letter[each.key].url
  redrive_allow_policy = jsonencode({ redrivePermission = "byQueue", sourceQueueArns = [aws_sqs_queue.consumer[each.key].arn] })
}
resource "aws_sqs_queue_policy" "consumer" {
  for_each  = local.consumers
  queue_url = aws_sqs_queue.consumer[each.key].url
  policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Effect    = "Allow", Principal = { Service = "sns.amazonaws.com" }, Action = "sqs:SendMessage",
    Resource  = aws_sqs_queue.consumer[each.key].arn,
    Condition = { ArnEquals = { "aws:SourceArn" = aws_sns_topic.events.arn } }
  }] })
}
resource "aws_sns_topic_subscription" "consumer" {
  for_each             = local.consumers
  topic_arn            = aws_sns_topic.events.arn
  protocol             = "sqs"
  endpoint             = aws_sqs_queue.consumer[each.key].arn
  raw_message_delivery = true
  depends_on           = [aws_sqs_queue_policy.consumer]
}
resource "aws_cloudwatch_metric_alarm" "dead_letter" {
  for_each            = local.consumers
  alarm_name          = "${local.prefix}-${each.key}-dlq"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 1
  metric_name         = "ApproximateNumberOfMessagesVisible"
  namespace           = "AWS/SQS"
  period              = 60
  statistic           = "Maximum"
  threshold           = 0
  treat_missing_data  = "notBreaching"
  dimensions          = { QueueName = aws_sqs_queue.dead_letter[each.key].name }
  alarm_actions       = var.alarm_topic_arn == null ? [] : [var.alarm_topic_arn]
}
resource "aws_cloudwatch_metric_alarm" "queue_age" {
  for_each            = local.consumers
  alarm_name          = "${local.prefix}-${each.key}-queue-age"
  comparison_operator = "GreaterThanThreshold"
  evaluation_periods  = 2
  metric_name         = "ApproximateAgeOfOldestMessage"
  namespace           = "AWS/SQS"
  period              = 60
  statistic           = "Maximum"
  threshold           = 300
  treat_missing_data  = "notBreaching"
  dimensions          = { QueueName = aws_sqs_queue.consumer[each.key].name }
  alarm_actions       = var.alarm_topic_arn == null ? [] : [var.alarm_topic_arn]
}
resource "aws_security_group" "cache" {
  count  = var.enable_cache ? 1 : 0
  name   = "${local.prefix}-cache"
  vpc_id = aws_vpc.main.id
}
resource "aws_vpc_security_group_ingress_rule" "cache" {
  count                        = var.enable_cache ? 1 : 0
  security_group_id            = aws_security_group.cache[0].id
  referenced_security_group_id = aws_security_group.tasks.id
  from_port                    = 6379
  to_port                      = 6380
  ip_protocol                  = "tcp"
}
resource "aws_elasticache_serverless_cache" "presence" {
  count                    = var.enable_cache ? 1 : 0
  name                     = "${local.prefix}-presence"
  engine                   = "valkey"
  major_engine_version     = "7"
  subnet_ids               = aws_subnet.private[*].id
  security_group_ids       = [aws_security_group.cache[0].id]
  kms_key_id               = aws_kms_key.data.arn
  daily_snapshot_time      = "03:00"
  snapshot_retention_limit = 1
}
