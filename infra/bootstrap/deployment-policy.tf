variable "create_deployment_policies" {
  description = "Create environment-specific infrastructure deployment policies when no external policy ARN is supplied."
  type        = bool
  default     = false
}
locals {
  deployment_enabled = var.deployment_policy_arn != null || var.create_deployment_policies
}
resource "aws_iam_policy" "deployment" {
  for_each    = var.create_deployment_policies && var.deployment_policy_arn == null ? toset(["stg", "prd"]) : toset([])
  name        = "${var.project_name}-${each.key}-terraform-deploy"
  description = "Terraform infrastructure deployment for ${var.project_name} ${each.key}"
  tags = {
    Environment   = each.key
    ResourceGroup = "${var.project_name}-${each.key}"
  }
  policy = jsonencode({ Version = "2012-10-17", Statement = [
    {
      Sid       = "RegionalInfrastructure", Effect = "Allow", Resource = "*",
      Action    = ["ec2:*", "ecs:*", "ecr:*", "elasticloadbalancing:*", "apigateway:*", "servicediscovery:*", "logs:*", "cloudwatch:*", "dynamodb:*", "kms:*", "sns:*", "sqs:*", "elasticache:*", "resource-groups:*", "tag:GetResources", "tag:GetTagKeys", "tag:GetTagValues"],
      Condition = { StringEquals = { "aws:RequestedRegion" = var.aws_region } }
    },
    {
      Sid      = "ProjectBuckets", Effect = "Allow", Action = ["s3:*"],
      Resource = ["arn:aws:s3:::${var.project_name}-${each.key}-*", "arn:aws:s3:::${var.project_name}-${each.key}-*/*"]
    },
    {
      Sid      = "TaskRoles", Effect = "Allow",
      Action   = ["iam:CreateRole", "iam:GetRole", "iam:DeleteRole", "iam:UpdateAssumeRolePolicy", "iam:TagRole", "iam:UntagRole", "iam:ListRoleTags", "iam:ListRolePolicies", "iam:ListAttachedRolePolicies", "iam:GetRolePolicy", "iam:PutRolePolicy", "iam:DeleteRolePolicy"],
      Resource = ["arn:aws:iam::*:role/${var.project_name}-${each.key}-*-task", "arn:aws:iam::*:role/${var.project_name}-${each.key}-*-execution"]
    },
    {
      Sid       = "ExecutionRolePolicy", Effect = "Allow", Action = ["iam:AttachRolePolicy", "iam:DetachRolePolicy"],
      Resource  = "arn:aws:iam::*:role/${var.project_name}-${each.key}-*-execution",
      Condition = { ArnEquals = { "iam:PolicyARN" = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy" } }
    },
    {
      Sid       = "PassTaskRoles", Effect = "Allow", Action = ["iam:PassRole"],
      Resource  = ["arn:aws:iam::*:role/${var.project_name}-${each.key}-*-task", "arn:aws:iam::*:role/${var.project_name}-${each.key}-*-execution"],
      Condition = { StringEquals = { "iam:PassedToService" = "ecs-tasks.amazonaws.com" } }
    },
    {
      Sid       = "ServiceLinkedRoles", Effect = "Allow", Action = ["iam:CreateServiceLinkedRole"], Resource = "*",
      Condition = { StringEquals = { "iam:AWSServiceName" = ["ecs.amazonaws.com", "elasticloadbalancing.amazonaws.com", "apigateway.amazonaws.com", "elasticache.amazonaws.com", "servicediscovery.amazonaws.com"] } }
    },
    {
      Sid    = "PrivateNamespace", Effect = "Allow", Resource = "*",
      Action = ["route53:CreateHostedZone", "route53:GetHostedZone", "route53:DeleteHostedZone", "route53:ListHostedZones", "route53:ListTagsForResource", "route53:ChangeTagsForResource", "route53:AssociateVPCWithHostedZone", "route53:DisassociateVPCFromHostedZone", "route53:GetChange"]
    }
  ] })
}
