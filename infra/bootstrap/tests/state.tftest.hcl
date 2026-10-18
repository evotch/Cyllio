mock_provider "aws" {}
run "protected_state" {
  command = plan
  variables { state_bucket_name = "cyllio-terraform-state-test" }
  assert {
    condition     = aws_s3_bucket_public_access_block.state.block_public_policy && aws_s3_bucket_public_access_block.state.restrict_public_buckets
    error_message = "Terraform state may not be publicly accessible."
  }
  assert {
    condition     = aws_s3_bucket_versioning.state.versioning_configuration[0].status == "Enabled" && !aws_s3_bucket.state.force_destroy
    error_message = "State must be versioned and must not be force-deleted."
  }
  assert {
    condition     = length(aws_iam_role.github) == 0
    error_message = "Bootstrap may not create deployment roles without an administrator-reviewed policy."
  }
}
run "environment_oidc_trust" {
  command = plan
  variables {
    state_bucket_name        = "cyllio-terraform-state-test"
    deployment_policy_arn    = "arn:aws:iam::123456789012:policy/CyllioDeployment"
    github_oidc_provider_arn = "arn:aws:iam::123456789012:oidc-provider/token.actions.githubusercontent.com"
  }
  assert {
    condition     = length(aws_iam_role.github) == 2 && length(aws_iam_openid_connect_provider.github) == 0
    error_message = "Create environment roles and reuse an existing account-wide OIDC provider."
  }
  assert {
    condition     = jsondecode(aws_iam_role.github["prd"].assume_role_policy).Statement[0].Condition.StringEquals["token.actions.githubusercontent.com:sub"] == "repo:evotch/Cyllio:environment:prd"
    error_message = "Production trust must be restricted to the production GitHub environment."
  }
  assert {
    condition = alltrue([for environment, role in aws_iam_role.github :
      role.tags.Environment == environment && role.tags.ResourceGroup == "cyllio-${environment}"
    ])
    error_message = "Environment deployment roles must override shared bootstrap tags."
  }
}
