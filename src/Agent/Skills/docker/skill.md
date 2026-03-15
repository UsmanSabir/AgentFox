# Docker Skill

## Overview

Provides Docker container operations for building, running, and managing containerized applications.

## Capabilities
- Container Management
- Image Building
- Container Deployment
- DevOps Operations
- Containerization

## Tools Provided
- `docker_build`: Build Docker images from Dockerfile
- `docker_run`: Run containers from images
- `docker_stop`: Stop running containers
- `docker_logs`: View container logs
- `docker_ps`: List running and stopped containers

## When to Use

Use this skill for:
- Building containerized applications
- Running containers for testing and deployment
- Managing application containers
- Monitoring container logs and status
- Integrating with deployment pipelines

## Best Practices

- Always build with descriptive tags (e.g., myapp:v1.0)
- Use meaningful container names for easy tracking
- Check running containers before performing operations
- Monitor logs for debugging and troubleshooting
- Stop containers gracefully before removing them
- Use environment-specific tags for deployment artifacts
- Keep images small and focused on single responsibilities
- Use multi-stage builds to reduce image size
- Document Dockerfile best practices and layer dependencies
