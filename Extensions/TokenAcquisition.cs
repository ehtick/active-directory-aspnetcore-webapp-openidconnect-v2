﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.AzureAD.UI;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace Microsoft.AspNetCore.Authentication
{
    /// <summary>
    /// Extension class enabling adding the TokenAcquisition service
    /// </summary>
    public static class TokenAcquisitionExtension
    {
        /// <summary>
        /// Add the token acquisition service.
        /// </summary>
        /// <param name="services">Service collection</param>
        /// <returns>the service collection</returns>
        public static IServiceCollection AddTokenAcquisition(this IServiceCollection services)
        {
            // Token acquisition service
            services.AddSingleton<ITokenAcquisition, TokenAcquisition>();
            return services;
        }
    }

    /// <summary>
    /// Token acquisition service
    /// </summary>
    public class TokenAcquisition : ITokenAcquisition
    {
        private AzureADOptions _azureAdOptions;

        private ITokenCacheProvider _tokenCacheProvider;

        /// <summary>
        /// Constructor of the TokenAcquisition service. This requires the Azure AD Options to 
        /// configure the confidential client application
        /// </summary>
        /// <param name="options">Options to configure the application</param>
        public TokenAcquisition(IOptionsMonitor<AzureADOptions> options, ITokenCacheProvider tokenCacheProvider)
        {
            _azureAdOptions = options.Get("AzureAD");
            _tokenCacheProvider = tokenCacheProvider;
        }

        /// <summary>
        /// The goal of this method is, when a user is authenticated, to add the user's account in the MSAL.NET cache
        /// so that this token can then be used to acquire a token on On-behalf-of the user befor call to downstream APIs.
        /// </summary>
        /// <param name="userAccessToken">Access token used to call this Web API</param>
        /// <example>
        /// From configuration of the Authentication of the ASP.NET Core Web API 
        /// <code>JwtBearerOptions option;</code>
        /// 
        /// Subscribe to the token validated event
        /// <code>
        /// options.Events = new JwtBearerEvents();
        /// options.Events.OnTokenValidated = OnTokenValidated;
        /// }
        /// </code>
        /// 
        /// And then in the OnTokenValidated method:
        /// <code>
        /// private async Task OnTokenValidated(TokenValidatedContext context)
        /// {
        ///  JwtSecurityToken accessToken = context.SecurityToken as JwtSecurityToken;
        ///  _tokenAcquisition.AddAccountToCache(accessToken);
        /// }
        /// </code>
        /// </example>
        public void AddAccountToCacheFromJwt(JwtSecurityToken jwtToken, IEnumerable<string> scopes)
        {
            if (jwtToken == null)
                throw new ArgumentNullException(nameof(jwtToken));

            if (scopes == null)
                throw new ArgumentNullException(nameof(scopes));

            try
            {
                UserAssertion userAssertion = new UserAssertion(jwtToken.RawData, "urn:ietf:params:oauth:grant-type:jwt-bearer");

                // .Result to make sure that the cache is filled-in before the controller tries to get access tokens
                AuthenticationResult result = Application.AcquireTokenOnBehalfOfAsync(scopes, userAssertion).Result;
                string acessTokenForGraphOBOUser = result.AccessToken;
            }
            catch (MsalException ex)
            {
                string message = ex.Message;
                throw;
            }
        }

        /// <summary>
        /// Add, to the MSAL.NET cache, the account of the user for which an authorization code was received when the Web API was called.
        /// An On-behalf-of token contained in the <see cref="AuthorizationCodeReceivedContext"/> is added to the cache, so that it can then be used to acquire another token on-behalf-of the 
        /// same user in order to call to downstream APIs.
        /// </summary>
        /// <param name="context">The context used when an 'AuthorizationCode' is received over the OpenIdConnect protocol.</param>
        /// <example>
        /// From the configuration of the Authentication of the ASP.NET Core Web API: 
        /// <code>OpenIdConnectOptions options;</code>
        /// 
        /// Subscribe to the authorization code recieved event:
        /// <code>
        ///  options.Events = new OpenIdConnectEvents();
        ///  options.Events.OnAuthorizationCodeReceived = OnAuthorizationCodeReceived;
        /// }
        /// </code>
        /// 
        /// And then in the OnAuthorizationCodeRecieved method, call <see cref="AddAccountToCacheFromAuthorizationCode"/>:
        /// <code>
        /// private async Task OnAuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
        /// {
        ///    await _tokenAcquisition.AddAccountToCacheFromAuthorizationCode(context, new string[] { "user.read" });
        /// }
        /// </code>
        /// </example>
        public async Task AddAccountToCacheFromAuthorizationCode(AuthorizationCodeReceivedContext context, IEnumerable<string> scopes)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (scopes == null)
                throw new ArgumentNullException(nameof(scopes));

            try
            {
                // As AcquireTokenByAuthorizationCodeAsync is asynchronous we want to tell ASP.NET core that we are handing the code
                // even if it's not done yet, so that it does not concurrently call the Token endpoint.
                context.HandleCodeRedemption();

                CreateApplicationIfNeeded(context.HttpContext, context.Principal, context.Properties, null);
                var result = await Application.AcquireTokenByAuthorizationCodeAsync(context.ProtocolMessage.Code, scopes);
                context.HandleCodeRedemption(result.AccessToken, result.IdToken);
            }
            catch (MsalException ex)
            {
                string message = ex.Message;
                throw;
            }
        }


        private ConfidentialClientApplication CreateApplicationIfNeeded(HttpContext httpContext, ClaimsPrincipal claimsPrincipal, AuthenticationProperties authenticationProperties, string signInScheme)
        {
            if (Application == null)
            {
                var request = httpContext.Request;
                var currentUri = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, request.Path);
                var credential = new ClientCredential(_azureAdOptions.ClientSecret);
                TokenCache userTokenCache = _tokenCacheProvider.GetCache(httpContext, claimsPrincipal, authenticationProperties, signInScheme);
                Application = new ConfidentialClientApplication(_azureAdOptions.ClientId, currentUri, credential, userTokenCache, null);
            }
            return Application;
        }

        /// <summary>
        /// Gets an access token for a downstream API on behalf of the user which evidence are provided by the
        /// <paramref name="user"/> parameter
        /// </summary>
        /// <param name="context">HttpContext (for instance of the controller)</param>
        /// <param name="user">Account described by its claims</param>
        /// <param name="scopes">Scopes to request for the downstream API to call</param>
        /// <returns>An access token to call the downstream API characterized by its scopes, on behalf of the user</returns>
        public async Task<string> GetAccessTokenOnBehalfOfUser(HttpContext context, string[] scopes)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (scopes == null)
                throw new ArgumentNullException(nameof(scopes));

            // Use MSAL to get the right token to call the API
            CreateApplicationIfNeeded(context, context.User, null, AzureADDefaults.CookieScheme);
            return await GetAccessTokenOnBehalfOfUser(context.User, scopes);
        }

        /// <summary>
        /// Gets an access token for a downstream API on behalf of the user described by its claimsPrincipal
        /// </summary>
        /// <param name="claimsPrincipal">Claims principal for the user on behalf of whom to get a token 
        /// <param name="scopes">Scopes for the downstream API to call</param>
        private async Task<string> GetAccessTokenOnBehalfOfUser(ClaimsPrincipal claimsPrincipal, string[] scopes)
        {
            string accountIdentifier = claimsPrincipal.GetAccountId();
            return await GetAccessTokenOnBehalfOfUser(accountIdentifier, scopes);
        }

        /// <summary>
        /// Gets an access token for a downstream API on behalf of the user which account ID is passed as an argument
        /// </summary>
        /// <param name="accountIdentifier">User account identifier for which to acquire a token. 
        /// See <see cref="Microsoft.Identity.Client.AccountId.Identifier"/></param>
        /// <param name="scopes">Scopes for the downstream API to call</param>
        private async Task<string> GetAccessTokenOnBehalfOfUser(string accountIdentifier, string[] scopes)
        {
            if (accountIdentifier == null)
                throw new ArgumentNullException(nameof(accountIdentifier));

            if (scopes == null)
                throw new ArgumentNullException(nameof(scopes));

            var accounts = await Application.GetAccountsAsync();

            try
            {
                AuthenticationResult result = null;
                var allAccounts = await Application.GetAccountsAsync();
                IAccount account = await Application.GetAccountAsync(accountIdentifier);
                result = await Application.AcquireTokenSilentAsync(scopes, account);
                return result.AccessToken;
            }
            catch (MsalException ex)
            {
                // TODO process the exception see if this is retryable etc ...
                throw;
            }
        }

        /// <summary>
        /// Access to the MSAL.NET confidential client application (for advanced scenarios)
        /// </summary>
        public ConfidentialClientApplication Application { get; private set; }

        // Todo provide a better cache
        static TokenCache userTokenCache = new TokenCache();
    }
}
