variable "github_repository" {
  type    = string
  default = "evotch/Cyllio"
}
variable "github_oidc_provider_arn" {
  description = "Existing GitHub OIDC provider ARN. Leave null to create one; there may be only one per AWS account."
  type        = string
  default     = null
}
variable "deployment_policy_arn" {
  description = "Administrator-reviewed managed policy permitting this Terraform stack and image publishing. Roles are not created until supplied."
  type        = string
  default     = null
}
resource "aws_iam_openid_connect_provider" "github" {
  count          = var.deployment_policy_arn != null && var.github_oidc_provider_arn == null ? 1 : 0
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
}
resource "aws_iam_role" "github" {
  for_each = var.deployment_policy_arn == null ? toset([]) : toset(["stg", "prd"])
  name     = "${var.project_name}-${each.key}-github-deploy"
  tags = {
    Project       = var.project_name
    Environment   = each.key
    ManagedBy     = "Terraform"
    Component     = "terraform-bootstrap"
    ResourceGroup = "${var.project_name}-${each.key}"
  }
  assume_role_policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Effect    = "Allow", Action = "sts:AssumeRoleWithWebIdentity",
    Principal = { Federated = var.github_oidc_provider_arn != null ? var.github_oidc_provider_arn : aws_iam_openid_connect_provider.github[0].arn },
    Condition = { StringEquals = {
      "token.actions.githubusercontent.com:aud" = "sts.amazonaws.com",
      "token.actions.githubusercontent.com:sub" = "repo:${var.github_repository}:environment:${each.key}"
    } }
  }] })
}
resource "aws_iam_role_policy_attachment" "deployment" {
  for_each   = aws_iam_role.github
  role       = each.value.name
  policy_arn = var.deployment_policy_arn
}
resource "aws_iam_role_policy" "state" {
  for_each = aws_iam_role.github
  role     = each.value.id
  policy = jsonencode({ Version = "2012-10-17", Statement = [
    { Effect = "Allow", Action = ["s3:ListBucket"], Resource = aws_s3_bucket.state.arn,
    Condition = { StringLike = { "s3:prefix" = ["${each.key}/*"] } } },
    { Effect = "Allow", Action = ["s3:GetObject", "s3:PutObject"], Resource = "${aws_s3_bucket.state.arn}/${each.key}/terraform.tfstate" },
    { Effect = "Allow", Action = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"], Resource = "${aws_s3_bucket.state.arn}/${each.key}/terraform.tfstate.tflock" }
  ] })
}
output "github_deployment_roles" { value = { for environment, role in aws_iam_role.github : environment => role.arn } }
