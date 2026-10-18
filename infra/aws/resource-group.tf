resource "aws_resourcegroups_group" "environment" {
  name        = local.prefix
  description = "${var.project_name} ${var.environment} resources managed with Terraform"
  tags        = merge(local.common_tags, { Name = local.prefix })

  resource_query {
    type = "TAG_FILTERS_1_0"
    query = jsonencode({
      ResourceTypeFilters = ["AWS::AllSupported"]
      TagFilters = [
        { Key = "Project", Values = [var.project_name] },
        { Key = "Environment", Values = [var.environment] }
      ]
    })
  }
}
