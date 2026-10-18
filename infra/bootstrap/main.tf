terraform {
  required_version = ">= 1.10, < 2.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}
provider "aws" {
  region = var.aws_region
  default_tags {
    tags = {
      Project     = var.project_name
      Environment = "shared"
      ManagedBy   = "Terraform"
      Component   = "terraform-bootstrap"
    }
  }
}
variable "project_name" {
  type    = string
  default = "cyllio"
}
variable "aws_region" {
  type    = string
  default = "us-east-1"
}
variable "state_bucket_names" {
  description = "Separate, globally unique S3 state bucket names for staging and production."
  type        = map(string)
  validation {
    condition = (
      toset(keys(var.state_bucket_names)) == toset(["stg", "prd"]) &&
      length(distinct(values(var.state_bucket_names))) == 2
    )
    error_message = "Supply distinct bucket names for exactly stg and prd."
  }
}
resource "aws_s3_bucket" "state" {
  for_each      = var.state_bucket_names
  bucket        = each.value
  force_destroy = false
  tags = {
    Environment   = each.key
    ResourceGroup = "${var.project_name}-${each.key}"
    Name          = each.value
  }
  lifecycle { prevent_destroy = true }
}
resource "aws_s3_bucket_versioning" "state" {
  for_each = var.state_bucket_names
  bucket   = aws_s3_bucket.state[each.key].id
  versioning_configuration { status = "Enabled" }
}
resource "aws_s3_bucket_public_access_block" "state" {
  for_each                = var.state_bucket_names
  bucket                  = aws_s3_bucket.state[each.key].id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}
resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  for_each = var.state_bucket_names
  bucket   = aws_s3_bucket.state[each.key].id
  rule {
    apply_server_side_encryption_by_default { sse_algorithm = "AES256" }
  }
}
resource "aws_s3_bucket_policy" "state" {
  for_each = var.state_bucket_names
  bucket   = aws_s3_bucket.state[each.key].id
  policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Sid       = "DenyInsecureTransport", Effect = "Deny", Principal = "*", Action = "s3:*",
    Resource  = [aws_s3_bucket.state[each.key].arn, "${aws_s3_bucket.state[each.key].arn}/*"],
    Condition = { Bool = { "aws:SecureTransport" = "false" } }
  }] })
}
output "state_buckets" { value = { for environment, bucket in aws_s3_bucket.state : environment => bucket.id } }
