variables {
  create_deployment_policies = false
  deployment_policy_arn      = null
}
mock_provider "aws" {
  mock_resource "aws_iam_policy" {
    override_during = plan
    defaults        = { arn = "arn:aws:iam::123456789012:policy/mock-deployment-policy" }
  }
}
override_resource {
  override_during = plan
  target          = aws_s3_bucket.state["stg"]
  values          = { arn = "arn:aws:s3:::cyllio-stg-state-test" }
}
override_resource {
  override_during = plan
  target          = aws_s3_bucket.state["prd"]
  values          = { arn = "arn:aws:s3:::cyllio-prd-state-test" }
}
run "protected_state" {
  command = plan
  variables {
    state_bucket_names = { stg = "cyllio-stg-state-test", prd = "cyllio-prd-state-test" }
  }
  assert {
    condition     = alltrue([for protection in aws_s3_bucket_public_access_block.state : protection.block_public_policy && protection.restrict_public_buckets])
    error_message = "Terraform state may not be publicly accessible."
  }
  assert {
    condition     = alltrue([for env, bucket in aws_s3_bucket.state : aws_s3_bucket_versioning.state[env].versioning_configuration[0].status == "Enabled" && !bucket.force_destroy])
    error_message = "State must be versioned and must not be force-deleted."
  }
  assert {
    condition = (
      length(aws_s3_bucket.state) == 2 &&
      aws_s3_bucket.state["stg"].bucket != aws_s3_bucket.state["prd"].bucket &&
      alltrue([for env, bucket in aws_s3_bucket.state : bucket.tags.Environment == env && bucket.tags.ResourceGroup == "cyllio-${env}"])
    )
    error_message = "Each environment needs its own correctly tagged state bucket."
  }
  assert {
    condition     = length(aws_iam_role.github) == 0
    error_message = "Bootstrap may not create deployment roles without an administrator-reviewed policy."
  }
}
run "environment_oidc_trust" {
  command = plan
  variables {
    state_bucket_names       = { stg = "cyllio-stg-state-test", prd = "cyllio-prd-state-test" }
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
  assert {
    condition = alltrue([for env, policy in aws_iam_role_policy.state :
      jsondecode(policy.policy).Statement[0].Resource == "arn:aws:s3:::cyllio-${env}-state-test" &&
      jsondecode(policy.policy).Statement[1].Resource == "arn:aws:s3:::cyllio-${env}-state-test/${env}/terraform.tfstate" &&
      jsondecode(policy.policy).Statement[2].Resource == "arn:aws:s3:::cyllio-${env}-state-test/${env}/terraform.tfstate.tflock"
    ])
    error_message = "Each role's state policy must reference only its own environment bucket."
  }
}

run "generated_environment_policies" {
  command = plan
  variables {
    state_bucket_names         = { stg = "cyllio-stg-state-test", prd = "cyllio-prd-state-test" }
    create_deployment_policies = true
    deployment_policy_arn      = null
    github_oidc_provider_arn   = "arn:aws:iam::123456789012:oidc-provider/token.actions.githubusercontent.com"
  }
  assert {
    condition     = length(aws_iam_policy.deployment) == 2 && length(aws_iam_role.github) == 2
    error_message = "Opt-in generated policies must create separate deployment roles and policies for both environments."
  }
  assert {
    condition = alltrue([for env, policy in aws_iam_policy.deployment :
      jsondecode(policy.policy).Statement[1].Resource[0] == "arn:aws:s3:::cyllio-${env}-*"
    ])
    error_message = "Generated deployment policies must scope S3 access by environment."
  }
}
