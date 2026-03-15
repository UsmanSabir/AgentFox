# Deployment Skill

## Overview

Provides comprehensive deployment and CI/CD operations for releasing applications to production and staging environments.

## Capabilities
- Application Deployment
- CI/CD Pipeline Management
- Release Management
- DevOps Automation
- Production Management

## Tools Provided
- `deploy`: Deploy applications to target environments
- `cicd_run`: Execute CI/CD pipelines

## When to Use

Use this skill for:
- Deploying applications to production or staging
- Running CI/CD pipelines
- Managing releases and versions
- Blue-green or canary deployments
- Post-deployment verification and monitoring

## Prerequisites

This skill requires:
- Git skill (for version control)
- Docker skill (for containerized deployments)

## Best Practices

- Always ensure all tests pass before deploying: run tests with coverage
- Create a release commit before deployment
- Use appropriate environment-specific settings
- Verify that dependent skills (git, docker) are working first
- Run CI/CD pipeline in dry-run mode before actual deployment
- Monitor deployment status and implement rollback procedures
- Document deployment steps and create runbooks for your team
- Use blue-green or canary deployment strategies for safety
- Implement automated health checks after deployment
- Maintain deployment logs for auditing and troubleshooting
