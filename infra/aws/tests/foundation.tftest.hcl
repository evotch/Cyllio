mock_provider "aws" {
  mock_data "aws_availability_zones" {
    defaults = { names = ["us-east-1a", "us-east-1b"] }
  }
  mock_data "aws_caller_identity" {
    defaults = { account_id = "123456789012" }
  }
  mock_data "aws_partition" {
    defaults = { partition = "aws" }
  }
}
run "staging_foundation" {
  command = plan
  variables { environment = "stg" }
  assert {
    condition = (
      aws_resourcegroups_group.environment.name == "cyllio-stg" &&
      jsondecode(aws_resourcegroups_group.environment.resource_query[0].query).TagFilters[0].Values[0] == "cyllio" &&
      jsondecode(aws_resourcegroups_group.environment.resource_query[0].query).TagFilters[1].Values[0] == "stg" &&
      length(jsondecode(aws_resourcegroups_group.environment.resource_query[0].query).TagFilters[1].Values) == 1
    )
    error_message = "Staging resource group must select only the correct project and staging environment."
  }
  assert {
    condition = alltrue([for name, repository in aws_ecr_repository.service :
      repository.tags.Project == "cyllio" && repository.tags.Environment == "stg" && repository.tags.Service == name && repository.tags.ResourceGroup == "cyllio-stg"
    ])
    error_message = "Service repositories must retain environment and service ownership tags."
  }
  assert {
    condition     = length(aws_ecr_repository.service) == 11 && length(aws_ecs_service.service) == 0
    error_message = "All repositories must exist before any application task starts."
  }
  assert {
    condition     = aws_lb.internal.internal && length(aws_subnet.private) == 2 && length(aws_nat_gateway.main) == 1
    error_message = "Staging must use private compute across two AZs with one NAT gateway."
  }
  assert {
    condition     = aws_s3_bucket_public_access_block.media.block_public_policy && aws_s3_bucket_public_access_block.media.restrict_public_buckets
    error_message = "Media must remain private."
  }
  assert {
    condition     = aws_dynamodb_table.main.point_in_time_recovery[0].enabled && aws_dynamodb_table.main.ttl[0].enabled
    error_message = "Durable state needs recovery and TTL configuration."
  }
  assert {
    condition     = length(aws_sqs_queue.consumer) == 3 && length(aws_sqs_queue.dead_letter) == 3
    error_message = "Each event consumer requires a dedicated queue and DLQ."
  }
}
run "production_foundation" {
  command = plan
  variables { environment = "prd" }
  assert {
    condition = (
      aws_resourcegroups_group.environment.name == "cyllio-prd" &&
      aws_resourcegroups_group.environment.tags.Environment == "prd" &&
      jsondecode(aws_resourcegroups_group.environment.resource_query[0].query).TagFilters[1].Values[0] == "prd" &&
      length(jsondecode(aws_resourcegroups_group.environment.resource_query[0].query).TagFilters[1].Values) == 1
    )
    error_message = "Production resource group must exclude staging resources."
  }
  assert {
    condition     = aws_dynamodb_table.main.deletion_protection_enabled && aws_lb.internal.enable_deletion_protection && length(aws_nat_gateway.main) == 2
    error_message = "Production must protect durable state and use an independent NAT gateway in each AZ."
  }
}
run "compatibility_services" {
  command = plan
  variables {
    environment        = "prd"
    enable_services    = true
    image_tag          = "0123456789abcdef"
    account_secret_arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:cyllio-prd-accounts-abcdef"
  }
  assert {
    condition     = length(aws_ecs_service.service) == 11 && aws_ecs_service.service["identity"].desired_count == 2
    error_message = "Production must run two tasks per service."
  }
  assert {
    condition     = alltrue([for service in aws_ecs_service.service : !service.network_configuration[0].assign_public_ip])
    error_message = "Tasks may not have public IPs."
  }
  assert {
    condition = alltrue([for name, service in aws_ecs_service.service :
      service.enable_ecs_managed_tags && service.propagate_tags == "SERVICE" && service.tags.Environment == "prd" && service.tags.Service == name
    ])
    error_message = "ECS must propagate environment/service tags to launched tasks."
  }
  assert {
    condition     = length(aws_iam_role_policy.account_secrets) == 2
    error_message = "Only Identity and Device execution roles may fetch account secrets."
  }
}
run "missing_compatibility_secret" {
  command = plan
  variables {
    environment     = "stg"
    enable_services = true
    image_tag       = "0123456789abcdef"
  }
  expect_failures = [aws_ecs_task_definition.service]
}
