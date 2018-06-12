# Secret variables

## Masked from logs

The agent masks secrets from the streaming output of steps within the job.

For example, consider the following definition:

```yaml
steps:
- powershell: |
    write-host '##vso[task.setvariable variable=mySecret;isSecret=true]abc123'
- powershell: |
    write-host 'The secret is: $(mySecret)'
```

The output from the second script is:

```
The secret is: ***
```

Secrets are masked before the streaming output is posted to the server, and before the output is written to the job logs on disk.

## Script steps and environment variables

By default, secret variables are not mapped into the environment block for script steps.

This is a precautionary measure to avoid inadvertently recording the values. For example, a crash dump or a downstream process logging environment variables.

The following definition illustrates the behavior:

```yaml
steps:
- powershell: |
    Write-Host '##vso[task.setvariable variable=mySecret;issecret=true]abc'
- powershell: |
    Write-Host "This works: $(mySecret)"
    Write-Host "This does not work: $env:MYSECRET"
    Write-Host "This works: $env:MY_MAPPED_ENV_VAR"
  env:
    MY_MAPPED_ENV_VAR: $(mySecret)
```

The output from the second script is:

```
This works: ***
This does not work:
This works: ***
```

Secret variables must explicitly be mapped-in for scripts, using macro syntax: `$(mySecret)`

__Security Note:__ Prefer mapping-in secrets using the `env` input rather than within the script contents. The `env` input sets an environment variable for the child process. Whereas the script contents are written to disk.
