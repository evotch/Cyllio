moved {
  from = aws_s3_bucket.state
  to   = aws_s3_bucket.state["stg"]
}

moved {
  from = aws_s3_bucket_versioning.state
  to   = aws_s3_bucket_versioning.state["stg"]
}

moved {
  from = aws_s3_bucket_public_access_block.state
  to   = aws_s3_bucket_public_access_block.state["stg"]
}

moved {
  from = aws_s3_bucket_server_side_encryption_configuration.state
  to   = aws_s3_bucket_server_side_encryption_configuration.state["stg"]
}

moved {
  from = aws_s3_bucket_policy.state
  to   = aws_s3_bucket_policy.state["stg"]
}
