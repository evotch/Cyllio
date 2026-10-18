output "ecr_repositories" {
  value = { for name, repository in aws_ecr_repository.service : name => repository.repository_url }
}
output "ecs_cluster" { value = aws_ecs_cluster.main.name }
output "api_base_url" { value = "${aws_apigatewayv2_api.http.api_endpoint}/api/v1" }
output "account_secret_arn" { value = var.account_secret_arn }
output "dynamodb_table" { value = aws_dynamodb_table.main.name }
output "dynamodb_stream_arn" { value = aws_dynamodb_table.main.stream_arn }
output "media_bucket" { value = aws_s3_bucket.media.id }
output "events_topic_arn" { value = aws_sns_topic.events.arn }
output "consumer_queue_urls" { value = { for name, queue in aws_sqs_queue.consumer : name => queue.url } }
output "cache_endpoint" { value = var.enable_cache ? aws_elasticache_serverless_cache.presence[0].endpoint : null }
output "services_enabled" { value = var.enable_services }
