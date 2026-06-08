using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace AwsRagChat.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IUserApprovalService _userApprovalService;
    private readonly IConfiguration _configuration;

    public AuthController(
        IUserApprovalService userApprovalService,
        IConfiguration configuration)
    {
        _userApprovalService = userApprovalService;
        _configuration = configuration;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        RegisterRequest request,
        [FromServices] IAmazonCognitoIdentityProvider cognito)
    {
        if (string.Equals(_configuration["CloudProvider"], "Azure", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "User registration is managed externally via Microsoft Entra ID portal or IT provisioning. API signup endpoint is not supported in Azure mode."
            });
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new
            {
                error = "Email and password are required."
            });
        }

        var userPoolId =
            _configuration["Cognito:UserPoolId"];

        if (string.IsNullOrWhiteSpace(userPoolId))
        {
            return BadRequest(new
            {
                error = "Cognito UserPoolId is missing."
            });
        }

        try
        {
            var createResponse = await cognito.AdminCreateUserAsync(
                new AdminCreateUserRequest
                {
                    UserPoolId = userPoolId,
                    Username = request.Email,
                    UserAttributes =
                    [
                        new AttributeType
                        {
                            Name = "email",
                            Value = request.Email
                        },
                        new AttributeType
                        {
                            Name = "email_verified",
                            Value = "true"
                        },
                        new AttributeType
                        {
                            Name = "custom:approvalStatus",
                            Value = ApprovalStatuses.Pending
                        }
                    ],
                    MessageAction = MessageActionType.SUPPRESS
                });

            await cognito.AdminSetUserPasswordAsync(
                new AdminSetUserPasswordRequest
                {
                    UserPoolId = userPoolId,
                    Username = request.Email,
                    Password = request.Password,
                    Permanent = true
                });

            await cognito.AdminConfirmSignUpAsync(
                new AdminConfirmSignUpRequest
                {
                    UserPoolId = userPoolId,
                    Username = request.Email
                });

            var userId = createResponse.User.Attributes
                .FirstOrDefault(attribute => attribute.Name == "sub")
                ?.Value ?? request.Email;

            await _userApprovalService.RegisterPendingUserAsync(
                userId,
                request.Email,
                HttpContext.RequestAborted);

            return Ok(new
            {
                message = "User registered successfully. Your account is pending admin approval."
            });
        }
        catch (UsernameExistsException)
        {
            return BadRequest(new
            {
                error = "User already exists."
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                error = ex.Message
            });
        }
    }
}

public class RegisterRequest
{
    public string Email { get; set; } = "";

    public string Password { get; set; } = "";

    public string Role { get; set; } = "";
}
