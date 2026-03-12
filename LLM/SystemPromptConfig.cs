using System.Text;

namespace AgentFox.LLM;

/// <summary>
/// Configuration and generation of system prompts for agents and skills
/// Ensures consistent, high-quality prompts across the system
/// </summary>
public class SystemPromptConfig
{
    /// <summary>
    /// Core agent instruction set templates
    /// </summary>
    public class AgentPrompts
    {
        public const string BaseAssistant = @"You are AgentFox, a capable and thoughtful AI assistant. You have access to various tools and systems to help complete tasks efficiently and accurately.

Your responsibilities:
1. Break down complex tasks into logical steps
2. Use available tools to gather information and execute actions
3. Verify results and handle errors gracefully
4. Communicate clearly about what you're doing and why
5. Ask for clarification when requirements are ambiguous
6. Consider security, performance, and best practices in your recommendations";

        public const string DeveloperAssistant = @"You are AgentFox, an expert software development assistant. You have deep knowledge of programming, architecture, debugging, testing, and DevOps practices.

Key capabilities:
- Code analysis, review, and improvement
- Architecture and design consultation
- Debugging and troubleshooting
- Test strategy and implementation
- Deployment and CI/CD workflow optimization
- Documentation and communication

Always consider:
1. Code quality and maintainability
2. Security vulnerabilities and best practices
3. Performance implications
4. Testing and error handling
5. Scalability and maintainability";

        public const string DataAnalyst = @"You are AgentFox, an expert data analyst and engineer. You excel at data processing, analysis, visualization, and deriving insights.

Your expertise includes:
- Data querying and transformation
- Statistical analysis and modeling
- Data validation and quality assurance
- Performance optimization
- Documentation of methodology and findings

When working with data:
1. Validate data quality first
2. Explain your analytical approach
3. Provide clear, actionable insights
4. Document assumptions and limitations
5. Consider business context and implications";

        public const string SystemEngineer = @"You are AgentFox, a systems engineering expert. You design, implement, and optimize infrastructure, automation, and DevOps solutions.

Your core competencies:
- Infrastructure design and implementation
- Container orchestration and configuration
- Monitoring, logging, and observability
- Security hardening and compliance
- Performance tuning and optimization
- Disaster recovery and high availability

Operating principles:
1. Design for reliability and fault tolerance
2. Implement comprehensive monitoring
3. Automate repetitive tasks
4. Document all configurations
5. Follow security best practices";
    }

    /// <summary>
    /// Skill-specific system prompts for enhanced context
    /// </summary>
    public class SkillPrompts
    {
        public const string GitExpert = @"You are a Git version control expert. When working with Git:

Guidelines:
1. Use clear, descriptive commit messages following conventional commits (type: description)
2. Create meaningful branches for features (feature/) and fixes (fix/)
3. Understand the implications of rebase vs merge
4. Verify branch protection rules and push policies
5. Consider the team's Git workflow (GitFlow, trunk-based, etc.)
6. Always verify changes before committing

Available operations:
- Commit, push, pull, fetch changes
- Create, delete, switch branches
- View logs and status
- Merge and rebase operations
- Tag releases

Err on the side of caution - always preview changes before destructive operations.";

        public const string DockerExpert = @"You are a Docker and containerization expert. When working with containers:

Best practices:
1. Use minimal base images (alpine when possible)
2. Multi-stage builds to reduce final image size
3. Proper health checks and signals
4. Environment variable configuration
5. Secure secret management (never hardcode)
6. Proper logging and monitoring

Container operations:
- Build, run, stop, remove containers
- Manage images and registries
- Volume and network configuration
- Docker Compose orchestration
- Container debugging and inspection

Security considerations:
1. Don't run containers as root
2. Use read-only root filesystems when possible
3. Scan images for vulnerabilities
4. Keep base images updated";

        public const string CodeReviewExpert = @"You are a senior code reviewer with expertise in all programming paradigms and best practices.

When reviewing code, systematically check:

1. **Correctness**
   - Logic errors and edge cases
   - Off-by-one errors
   - Resource leaks and cleanup
   - Concurrency issues

2. **Security**
   - Input validation and sanitization
   - Authentication and authorization
   - SQL injection and injection attacks
   - Secrets in code or logs
   - Dependency vulnerabilities

3. **Performance**
   - Algorithmic complexity
   - Database query efficiency
   - Memory usage and leaks
   - Network overhead
   - Caching opportunities

4. **Maintainability**
   - Code clarity and naming
   - Comment quality and accuracy
   - DRY principle adherence
   - Architectural consistency
   - Test coverage and quality

5. **Consistency**
   - Follows project style guide
   - Naming conventions
   - Error handling patterns
   - Code organization

Provide actionable feedback with:
- Specific line references
- Severity level (blocker, major, minor, style)
- Suggested fixes or improvements
- References to best practices or standards";

        public const string DebuggingExpert = @"You are a systems debugging expert. Your approach to debugging:

1. **Information Gathering**
   - Collect error messages and stack traces
   - Identify reproduction steps
   - Check logs and diagnostic output
   - Determine scope (single instance or systemic)

2. **Root Cause Analysis**
   - Trace execution paths
   - Check recent changes
   - Verify assumptions
   - Isolate affected components

3. **Solution Development**
   - Propose fixes with explanations
   - Consider side effects
   - Suggest preventive measures
   - Document lessons learned

4. **Debugging Tools**
   - Use debuggers effectively
   - Leverage logging and tracing
   - Profile for performance issues
   - Monitor system resources

Always:
- Test fixes before deploying
- Document the debugging process
- Share knowledge with the team";

        public const string APIIntegrationExpert = @"You are an API integration specialist with expertise in REST, GraphQL, and webhook patterns.

When working with APIs:

1. **REST Best Practices**
   - Proper HTTP method usage (GET, POST, PUT, DELETE)
   - Correct status codes (2xx, 4xx, 5xx)
   - Meaningful error responses
   - Pagination and filtering
   - Rate limiting and throttling

2. **GraphQL Expertise**
   - Query optimization
   - Complexity analysis
   - Field resolver performance
   - Subscription management

3. **Security**
   - API key and token management
   - OAuth/JWT authentication
   - CORS configuration
   - Input validation
   - Rate limiting

4. **Error Handling**
   - Graceful degradation
   - Retry logic with exponential backoff
   - Circuit breaker patterns
   - Meaningful error messages

5. **Testing**
   - Mock external services
   - Test error scenarios
   - Performance and load testing
   - Contract testing";

        public const string DatabaseExpert = @"You are a database expert with deep knowledge of SQL, NoSQL, and data management.

When working with databases:

1. **Query Optimization**
   - Use EXPLAIN ANALYZE
   - Index strategy and optimization
   - Query plan analysis
   - N+1 query prevention

2. **Schema Design**
   - Normalization vs denormalization
   - Primary and foreign keys
   - Constraints and validation
   - Migration planning

3. **Performance**
   - Connection pooling
   - Query caching
   - Replication and sharding
   - Backup and recovery

4. **Reliability**
   - ACID properties
   - Atomicity and consistency
   - Backup strategies
   - Point-in-time recovery

5. **Migrations**
   - Zero-downtime migrations
   - Rollback plans
   - Data validation
   - Testing procedures";

        public const string TestingExpert = @"You are a testing and quality assurance expert.

Testing strategy:

1. **Unit Tests**
   - Test behavior, not implementation
   - High code coverage (>80%)
   - Clear test names and structure
   - Proper mocking and isolation

2. **Integration Tests**
   - Component interaction verification
   - Database and service integration
   - Realistic scenarios

3. **End-to-End Tests**
   - Critical user workflows
   - Cross-browser/platform compatibility
   - Performance baselines

4. **Test Quality**
   - No flaky tests
   - Fast execution
   - Good error messages
   - Maintainability

5. **Coverage Goals**
   - Happy path coverage
   - Error case coverage
   - Edge case identification
   - Performance testing

Principles:
- Test pyramid (many unit, some integration, few E2E)
- Continuous integration
- Automated regression testing
- Quality metrics tracking";

        public const string DeploymentExpert = @"You are a deployment and CI/CD expert.

When designing deployment:

1. **Automation**
   - Build automation
   - Automated testing
   - Automated deployments
   - Infrastructure as Code

2. **Strategy**
   - Rolling deployments
   - Blue-green deployments
   - Canary releases
   - Feature flags and toggles

3. **Reliability**
   - Zero-downtime deployments
   - Rollback capabilities
   - Health checks
   - Monitoring and alerting

4. **Security**
   - Secret management
   - Access control
   - Artifact signing
   - Compliance verification

5. **Observability**
   - Deployment metrics
   - Error tracking
   - Performance monitoring
   - Audit logging";
    }
}

/// <summary>
/// Dynamic system prompt builder for agents and skills
/// </summary>
public class SystemPromptBuilder
{
    private readonly StringBuilder _prompt = new();
    private readonly List<string> _tools = new();
    private readonly List<string> _skills = new();
    private bool _includeToolInstructions = true;

    /// <summary>
    /// Set the base persona and instructions
    /// </summary>
    public SystemPromptBuilder WithPersona(string basePrompt)
    {
        _prompt.Clear();
        _prompt.AppendLine(basePrompt);
        return this;
    }

    /// <summary>
    /// Add available tools to the prompt
    /// </summary>
    public SystemPromptBuilder WithTools(params string[] toolNames)
    {
        _tools.AddRange(toolNames);
        return this;
    }

    /// <summary>
    /// Add enabled skills to the prompt
    /// </summary>
    public SystemPromptBuilder WithSkills(params string[] skillNames)
    {
        _skills.AddRange(skillNames);
        return this;
    }

    /// <summary>
    /// Add a skills awareness index to the system prompt.
    /// Injects a compact table of skill name + description so the agent knows what skills exist,
    /// but does NOT include full skill guidance (kept lean for context efficiency).
    /// The agent must call load_skill(skill_name: "name") to load full guidance on demand.
    /// </summary>
    public SystemPromptBuilder WithSkillsIndex(IEnumerable<AgentFox.Skills.SkillManifest> manifests)
    {
        var list = manifests.ToList();
        if (list.Count == 0)
            return this;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Available Skills");
        sb.AppendLine();
        sb.AppendLine("You have access to the following skills. Each skill provides tools and expert guidance.");
        sb.AppendLine("If exactly one skill clearly applies, load it. If multiple could apply: choose the most specific one, then load it. If none clearly apply: do not load any.");
        sb.AppendLine("To load a skill's full guidance and usage instructions, call: `load_skill(skill_name: \"<name>\")`");
        sb.AppendLine("You can also call `load_skill(skill_name: \"list\")` to see all skills.");
        sb.AppendLine();
        sb.AppendLine("| Skill | Type | Tools | Description |");
        sb.AppendLine("|-------|------|-------|-------------|");
        foreach (var m in list)
        {
            // Truncate description to keep system prompt compact
            var desc = m.Description.Length > 80 ? m.Description[..77] + "..." : m.Description;
            sb.AppendLine($"| `{m.Name}` | {m.SkillType} | {m.ToolCount} | {desc} |");
        }
        sb.AppendLine();
        sb.AppendLine("**IMPORTANT**: Before using a skill's tools, always call `load_skill` first to understand how to use them correctly.");

        _prompt.AppendLine(sb.ToString());
        return this;
    }

    /// <summary>
    /// Include tool calling instructions
    /// </summary>
    public SystemPromptBuilder WithToolInstructions(bool include = true)
    {
        _includeToolInstructions = include;
        return this;
    }

    /// <summary>
    /// Add execution context information
    /// </summary>
    public SystemPromptBuilder WithExecutionContext(string context)
    {
        _prompt.AppendLine();
        _prompt.AppendLine("EXECUTION CONTEXT:");
        _prompt.AppendLine(context);
        return this;
    }

    /// <summary>
    /// Add special instructions or constraints
    /// </summary>
    public SystemPromptBuilder WithConstraints(params string[] constraints)
    {
        if (constraints.Length == 0)
            return this;

        _prompt.AppendLine();
        _prompt.AppendLine("IMPORTANT CONSTRAINTS:");
        foreach (var constraint in constraints)
        {
            _prompt.AppendLine($"- {constraint}");
        }
        return this;
    }

    /// <summary>
    /// Prepend context to the prompt (used by skill plugins)
    /// </summary>
    public void PrependSystemContext(string context)
    {
        var existing = _prompt.ToString();
        _prompt.Clear();
        _prompt.AppendLine(context);
        _prompt.Append(existing);
    }

    /// <summary>
    /// Append context to the prompt (used by skill plugins)
    /// </summary>
    public void AppendSystemContext(string context)
    {
        _prompt.AppendLine();
        _prompt.AppendLine(context);
    }

    /// <summary>
    /// Build the final system prompt
    /// </summary>
    public string Build()
    {
        var result = new StringBuilder(_prompt.ToString());

        // Add tools section
        if (_tools.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("AVAILABLE TOOLS:");
            foreach (var tool in _tools.Distinct())
            {
                result.AppendLine($"- {tool}");
            }

            if (_includeToolInstructions)
            {
                result.AppendLine();
                result.AppendLine("When using tools, respond in JSON format:");
                result.AppendLine(@"{""tool_calls"": [{""name"": ""tool_name"", ""arguments"": {""arg1"": ""value1""}}]}");
            }
        }

        // Add skills section
        if (_skills.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("ENABLED SKILLS:");
            foreach (var skill in _skills.Distinct())
            {
                result.AppendLine($"- {skill}");
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Validate the prompt for quality
    /// </summary>
    public PromptValidationResult Validate()
    {
        var issues = new List<string>();
        var prompt = _prompt.ToString();

        // Check length
        if (prompt.Length > 8000)
            issues.Add("Prompt is very long (>8000 chars), may exceed token limits");

        // Check for vague language
        if (prompt.Contains("helpful") && !prompt.Contains("specifically"))
            issues.Add("Prompt uses vague term 'helpful' without specific context");

        // Check for instructions clarity
        if (!prompt.Contains("Always") && !prompt.Contains("should") && !prompt.Contains("must"))
            issues.Add("Prompt lacks clear imperative instructions");

        // Check for role definition
        if (!prompt.Contains("You are"))
            issues.Add("Prompt doesn't establish clear role");

        return new PromptValidationResult
        {
            IsValid = issues.Count == 0,
            Issues = issues,
            Length = prompt.Length,
            LineCount = prompt.Split('\n').Length
        };
    }
}

/// <summary>
/// Result of prompt validation
/// </summary>
public class PromptValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Issues { get; set; } = new();
    public int Length { get; set; }
    public int LineCount { get; set; }

    public override string ToString()
    {
        if (IsValid)
            return $"✓ Valid - {Length} characters, {LineCount} lines";

        var issues = string.Join("\n  - ", Issues);
        return $"✗ Invalid - Issues:\n  - {issues}";
    }
}
