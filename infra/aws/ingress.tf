resource "aws_lb" "internal" {
  name                       = local.prefix
  internal                   = true
  load_balancer_type         = "application"
  subnets                    = aws_subnet.private[*].id
  security_groups            = [aws_security_group.alb.id]
  drop_invalid_header_fields = true
  enable_deletion_protection = var.environment == "prd"
}
resource "aws_lb_target_group" "api" {
  name        = "${local.prefix}-api"
  port        = 8080
  protocol    = "HTTP"
  target_type = "ip"
  vpc_id      = aws_vpc.main.id
  health_check {
    path                = "/health/ready"
    matcher             = "200"
    interval            = 30
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}
resource "aws_lb_listener" "api" {
  load_balancer_arn = aws_lb.internal.arn
  port              = 80
  protocol          = "HTTP"
  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}
resource "aws_apigatewayv2_api" "http" {
  name          = "${local.prefix}-http"
  protocol_type = "HTTP"
}
resource "aws_apigatewayv2_vpc_link" "main" {
  name               = local.prefix
  subnet_ids         = aws_subnet.private[*].id
  security_group_ids = [aws_security_group.link.id]
}
resource "aws_apigatewayv2_integration" "api" {
  api_id                 = aws_apigatewayv2_api.http.id
  integration_type       = "HTTP_PROXY"
  integration_uri        = aws_lb_listener.api.arn
  integration_method     = "ANY"
  connection_type        = "VPC_LINK"
  connection_id          = aws_apigatewayv2_vpc_link.main.id
  payload_format_version = "1.0"
  request_parameters     = { "overwrite:path" = "$request.path" }
}
resource "aws_apigatewayv2_route" "api" {
  for_each           = toset(["ANY /api/v1/{proxy+}", "GET /health/ready"])
  api_id             = aws_apigatewayv2_api.http.id
  route_key          = each.value
  target             = "integrations/${aws_apigatewayv2_integration.api.id}"
  authorization_type = "NONE" # Identity/Device validate bearer tokens; OTP routes are anonymous.
}
resource "aws_cloudwatch_log_group" "http" {
  name              = "/apigateway/${local.prefix}/http"
  retention_in_days = var.log_retention_days
}
resource "aws_apigatewayv2_stage" "http" {
  api_id      = aws_apigatewayv2_api.http.id
  name        = "$default"
  auto_deploy = true
  default_route_settings {
    throttling_burst_limit = 100
    throttling_rate_limit  = 50
  }
  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.http.arn
    format          = jsonencode({ requestId = "$context.requestId", routeKey = "$context.routeKey", status = "$context.status", integrationStatus = "$context.integrationStatus" })
  }
}
