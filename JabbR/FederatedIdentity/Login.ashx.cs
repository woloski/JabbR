﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IdentityModel.Selectors;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Security;
using System.Xml;
using JabbR.App_Start;
using JabbR.Services;
using Microsoft.IdentityModel.Claims;
using Microsoft.IdentityModel.Protocols.WSFederation;
using Microsoft.IdentityModel.Protocols.WSTrust;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Web;
using Ninject;

namespace JabbR.FederatedIdentity
{
    /// <summary>
    /// Endpoint that will accept a SAML 1.1 or 2.0 assertion via WS-Federation protocol and generates a Jabbr user
    /// </summary>
    public class Login : IHttpHandler
    {
        public bool IsReusable
        {
            get
            {
                return false;
            }
        }

        public void ProcessRequest(HttpContext context)
        {
            var settings = Bootstrapper.Kernel.Get<IApplicationSettings>();
            if (settings.FedAuthRequiresSsl && !HttpContext.Current.Request.IsSecureConnection)
            {
                throw new InvalidOperationException("This endpoints works over TLS/SSL only");
            }

            // validate SAML token
            var request = HttpContext.Current.Request;
            var tokenXml = GetTokenXml(request);
            var handlers = CreateSecurityTokenHandlerCollection(settings);
            var token = handlers.ReadToken(XmlReader.Create(new StringReader(tokenXml)));
            var identities = handlers.ValidateToken(token);
            var claims = ClaimsPrincipal.CreateFromIdentities(identities);

            IClaimsIdentity identity = claims.Identities[0];

            // link identity based on token claims
            var userInfo = GetUserInfoFromClaims(identity);
            var identityLinker = Bootstrapper.Kernel.Get<IIdentityLinker>();
            identityLinker.LinkIdentity(new HttpContextWrapper(context), userInfo.UserId, userInfo.Username, userInfo.Email);

            // redirect to original url if any
            string hash = context.Request.Form["wctx"];
            context.Response.Redirect(GetUrl(hash), false);
            context.ApplicationInstance.CompleteRequest();
        }

        private static UserInfo GetUserInfoFromClaims(IClaimsIdentity identity)
        {
            var userIdentity = GetAnyOfTheseClaims(identity, ClaimTypes.NameIdentifier, ClaimTypes.Name, ClaimTypes.Email, ClaimTypes.Prip.Email);
            if (userIdentity == null)
            {
                throw new InvalidOperationException(String.Format("The user id could not be derived from current claims: {0}", String.Join("; ", identity.Claims.Select(c => c.ClaimType).ToArray())));
            }

            var username = GetAnyOfTheseClaims(identity, ClaimTypes.Name, ClaimTypes.GivenName, ClaimTypes.Surname, ClaimTypes.Email, ClaimTypes.Prip.Email);
            if (String.IsNullOrEmpty(username))
            {
                throw new InvalidOperationException(String.Format("The username could not be derived from current claims: {0}", String.Join("; ", identity.Claims.Select(c => c.ClaimType).ToArray())));
            }

            var email = GetAnyOfTheseClaims(identity, ClaimTypes.Email, ClaimTypes.Prip.Email);

            return new UserInfo
            {
                UserId = userIdentity,
                Username = username,
                Email = email
            };
        }

        private static string GetAnyOfTheseClaims(IClaimsIdentity identity, params string[] claimTypes)
        {
            // try first with the specified claimtypes
            foreach (var claimType in claimTypes)
            {
                Claim claim = identity.Claims.SingleOrDefault(c => c.ClaimType.Equals(claimType, StringComparison.OrdinalIgnoreCase));
                if (claim != null)
                {
                    // return the first claimtype found
                    return claim.Value;
                }
            }

            return null;
        }

        private static SecurityTokenHandlerCollection CreateSecurityTokenHandlerCollection(IApplicationSettings settings)
        {
            var config = new SecurityTokenHandlerConfiguration();
            config.AudienceRestriction.AllowedAudienceUris.Add(new Uri(settings.FedAuthRealm));
            config.CertificateValidator = X509CertificateValidator.None;
            config.IssuerNameRegistry = new CustomIssuerNameRegistry(settings.FedAuthCertificateThumbprint);
            var handlers = SecurityTokenHandlerCollection.CreateDefaultSecurityTokenHandlerCollection(config);
            handlers.AddOrReplace(new MachineKeySessionSecurityTokenHandler());
            return handlers;
        }

        private static string GetTokenXml(HttpRequest request)
        {
            var quotas = new XmlDictionaryReaderQuotas();
            quotas.MaxArrayLength = 0x200000;
            quotas.MaxStringContentLength = 0x200000;

            var wsFederationMessage = WSFederationMessage.CreateFromFormPost(request) as SignInResponseMessage;
            WSFederationSerializer federationSerializer;
            using (var reader = XmlDictionaryReader.CreateTextReader(Encoding.UTF8.GetBytes(wsFederationMessage.Result), quotas))
            {
                federationSerializer = new WSFederationSerializer(reader);
            }

            var serializationContext = new WSTrustSerializationContext(SecurityTokenHandlerCollectionManager.CreateDefaultSecurityTokenHandlerCollectionManager());
            var tokenXml = federationSerializer.CreateResponse(wsFederationMessage, serializationContext).RequestedSecurityToken.SecurityTokenXml.OuterXml;
            return tokenXml;
        }

        private string GetUrl(string hash)
        {
            return HttpRuntime.AppDomainAppVirtualPath + hash;
        }

        private class UserInfo
        {
            public string Username { get; set; }
            public string UserId { get; set; }
            public string Email { get; set; }
        }

        private class CustomIssuerNameRegistry : IssuerNameRegistry
        {
            private readonly List<string> trustedThumbrpints = new List<string>();

            public CustomIssuerNameRegistry(string trustedThumbprint)
            {
                this.trustedThumbrpints.Add(trustedThumbprint);
            }

            public void AddTrustedIssuer(string thumbprint)
            {
                this.trustedThumbrpints.Add(thumbprint);
            }

            public override string GetIssuerName(System.IdentityModel.Tokens.SecurityToken securityToken)
            {
                var x509 = securityToken as X509SecurityToken;
                if (x509 != null)
                {
                    foreach (string thumbprint in trustedThumbrpints)
                    {
                        if (x509.Certificate.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
                        {
                            return x509.Certificate.Subject;
                        }
                    }
                }

                return null;
            }
        }

        private class MachineKeySessionSecurityTokenHandler : SessionSecurityTokenHandler
        {
            public MachineKeySessionSecurityTokenHandler()
                : base(CreateTransforms())
            { }

            public MachineKeySessionSecurityTokenHandler(SecurityTokenCache cache, TimeSpan tokenLifetime)
                : base(CreateTransforms(), cache, tokenLifetime)
            { }

            private static ReadOnlyCollection<CookieTransform> CreateTransforms()
            {
                return new List<CookieTransform>
                {
                    new DeflateCookieTransform(),
                    new MachineKeyCookieTransform()
                }.AsReadOnly();
            }

            private class MachineKeyCookieTransform : CookieTransform
            {
                public override byte[] Decode(byte[] encoded)
                {
                    return MachineKey.Decode(Encoding.UTF8.GetString(encoded), MachineKeyProtection.All);
                }

                public override byte[] Encode(byte[] value)
                {
                    return Encoding.UTF8.GetBytes(MachineKey.Encode(value, MachineKeyProtection.All));
                }
            }
        }
    }
}