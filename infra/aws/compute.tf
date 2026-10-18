resource "aws_ecr_repository" "service" {
  tags                 = merge(local.common_tags, { Service = each.key })
  for_each             = local.services
  name                 = "${local.prefix}/${each.value.image}"
  image_tag_mutability = "IMMUTABLE"
  image_scanning_configuration { scan_on_push = true }
  encryption_configuration { encryption_type = "AES256" }
}
resource "aws_ecr_lifecycle_policy" "service" {
  for_each   = local.services
  repository = aws_ecr_repository.service[each.key].name
  policy = jsonencode({ rules = [{ rulePriority = 1, description = "Expire untagged images after 14 days", selection = {
    tagStatus = "untagged", countType = "sinceImagePushed", countUnit = "days", countNumber = 14
  }, action = { type = "expire" } }] })
}
resource "aws_ecs_cluster" "main" {
  name = local.prefix
  setting {
    name  = "containerInsights"
    value = "enabled"
  }
}
resource "aws_service_discovery_private_dns_namespace" "main" {
  name = local.namespace
  vpc  = aws_vpc.main.id
}
resource "aws_service_discovery_service" "service" {
  tags     = merge(local.common_tags, { Service = each.key })
  for_each = local.deployed
  name     = each.key
  dns_config {
    namespace_id   = aws_service_discovery_private_dns_namespace.main.id
    routing_policy = "MULTIVALUE"
    dns_records {
      ttl  = 10
      type = "A"
    }
  }
  health_check_custom_config {}
}
resource "aws_cloudwatch_log_group" "service" {
  tags              = merge(local.common_tags, { Service = each.key })
  for_each          = local.services
  name              = "/ecs/${local.prefix}/${each.key}"
  retention_in_days = var.log_retention_days
}
resource "aws_iam_role" "execution" {
  tags     = merge(local.common_tags, { Service = each.key })
  for_each = local.services
  name     = "${local.prefix}-${each.key}-execution"
  assume_role_policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Effect = "Allow", Principal = { Service = "ecs-tasks.amazonaws.com" }, Action = "sts:AssumeRole"
  }] })
}
resource "aws_iam_role_policy_attachment" "execution" {
  for_each   = local.services
  role       = aws_iam_role.execution[each.key].name
  policy_arn = "arn:${data.aws_partition.current.partition}:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}
resource "aws_iam_role_policy" "account_secrets" {
  for_each = var.account_secret_arn == null ? toset([]) : local.account_services
  role     = aws_iam_role.execution[each.key].id
  policy = jsonencode({ Version = "2012-10-17", Statement = concat([
    { Effect = "Allow", Action = ["secretsmanager:GetSecretValue"], Resource = var.account_secret_arn }
    ], var.account_secret_kms_key_arn == null ? [] : [
    { Effect = "Allow", Action = ["kms:Decrypt"], Resource = var.account_secret_kms_key_arn }
  ]) })
}
resource "aws_iam_role" "task" {
  tags     = merge(local.common_tags, { Service = each.key })
  for_each = local.services
  name     = "${local.prefix}-${each.key}-task"
  assume_role_policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Effect = "Allow", Principal = { Service = "ecs-tasks.amazonaws.com" }, Action = "sts:AssumeRole"
  }] })
}
resource "aws_ecs_task_definition" "service" {
  tags                     = merge(local.common_tags, { Service = each.key })
  for_each                 = local.deployed
  family                   = "${local.prefix}-${each.key}"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.task_cpu
  memory                   = var.task_memory
  execution_role_arn       = aws_iam_role.execution[each.key].arn
  task_role_arn            = aws_iam_role.task[each.key].arn
  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
  container_definitions = jsonencode([{
    name         = each.key, image = "${aws_ecr_repository.service[each.key].repository_url}:${var.image_tag}", essential = true
    portMappings = [{ name = "http", containerPort = 8080, protocol = "tcp" }]
    environment = concat([
      { name = "ASPNETCORE_ENVIRONMENT", value = var.environment == "prd" ? "Production" : "Staging" },
      { name = "ASPNETCORE_HTTP_PORTS", value = "8080" },
      { name = "AWS_REGION", value = var.aws_region },
      { name = "Auth__EnableDevelopmentOtp", value = "false" }
      ], each.key == "api" ? [
      { name = "Services__Identity", value = "http://identity.${local.namespace}:8080/" },
      { name = "Services__Device", value = "http://device.${local.namespace}:8080/" }
      ] : [], each.key == "identity" && var.enable_sms ? concat([
        { name = "Auth__Sms__Enabled", value = "true" },
        { name = "Auth__Sms__Region", value = var.aws_region },
        { name = "Auth__Sms__ConfigurationSet", value = aws_pinpointsmsvoicev2_configuration_set.otp[0].name }
        ], [for country, arn in var.sms_origination_identities : {
          name = "Auth__Sms__OriginationIdentities__${country}", value = arn
    }]) : [])
    secrets = contains(local.account_services, each.key) ? local.account_secrets : []
    logConfiguration = { logDriver = "awslogs", options = {
      awslogs-group = aws_cloudwatch_log_group.service[each.key].name, awslogs-region = var.aws_region, awslogs-stream-prefix = "ecs"
    } }
  }])
  lifecycle {
    precondition {
      condition     = var.image_tag != "" && var.image_tag != "latest"
      error_message = "Enable services only with a pushed immutable image tag."
    }
    precondition {
      condition     = var.account_secret_arn != null
      error_message = "Current account services still need the MongoDB compatibility secret. DynamoDB application migration is pending."
    }
  }
}
resource "aws_ecs_service" "service" {
  enable_ecs_managed_tags = true
  propagate_tags          = "SERVICE"
  tags                    = merge(local.common_tags, { Service = each.key })
  for_each                = local.deployed
  name                    = "${local.prefix}-${each.key}"
  cluster                 = aws_ecs_cluster.main.id
  task_definition         = aws_ecs_task_definition.service[each.key].arn
  desired_count           = var.environment == "prd" ? 2 : 1
  launch_type             = "FARGATE"
  platform_version        = "1.4.0"
  wait_for_steady_state   = true
  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }
  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.tasks.id]
    assign_public_ip = false
  }
  service_registries { registry_arn = aws_service_discovery_service.service[each.key].arn }
  dynamic "load_balancer" {
    for_each = each.key == "api" ? [1] : []
    content {
      target_group_arn = aws_lb_target_group.api.arn
      container_name   = each.key
      container_port   = 8080
    }
  }
  depends_on = [aws_lb_listener.api, aws_iam_role_policy_attachment.execution, aws_iam_role_policy.account_secrets, aws_iam_role_policy.identity_sms, aws_pinpointsmsvoicev2_event_destination.otp, aws_route_table_association.private]
}
