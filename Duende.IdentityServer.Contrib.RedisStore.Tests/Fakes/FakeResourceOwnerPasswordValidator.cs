using Duende.IdentityServer.Validation;
using IdentityModel;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Duende.IdentityServer.Contrib.RedisStore.Tests.Fakes
{
    class FakeResourceOwnerPasswordValidator : IResourceOwnerPasswordValidator
    {
        public Task ValidateAsync(ResourceOwnerPasswordValidationContext context)
        {
            context.Result = new GrantValidationResult(subject: "1",
                authenticationMethod: OidcConstants.AuthenticationMethods.Password,
                claims: new List<Claim> { });

            return Task.CompletedTask;
        }
    }
}
