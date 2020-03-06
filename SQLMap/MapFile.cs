using System;
using System.Collections.Generic;
using System.Text;
using FluentValidation;

namespace SQLMaps
{
    public class MapFile
    {
        public string Description { get; set; }
        public string Author { get; set; }
        public string Email { get; set; }
        public string Id { get; set; }
        public double Version { get; set; }

        /// <summary>
        /// Value to prepend to CSV file names. Useful to group results together
        /// </summary>
        public string CSVPrefix { get; set; }

        /// <summary>
        /// The database's 'normal' filename to look
        /// </summary>
        public string FileName { get; set; }
        

        /// <summary>
        /// The SQL statement used to identify this database
        /// </summary>
        public string IdentifyQuery { get; set; }
        /// <summary>
        /// The expected value of IdentityQuery
        /// <remarks>This is always treated as a string</remarks>
        /// </summary>
        public string IdentifyValue { get; set; }

        public List<QueryInfo> Queries { get; set; }

        public override string ToString()
        {
            return
                $"Description: {Description} Version: {Version} Author: {Author} Email: {Email}, Query count: {Queries.Count:N0} Filename: {FileName} ";
        }
    }

    public class QueryInfo
    {
        public string Name { get; set; }
        public string Query { get; set; }

        public string BaseFileName { get; set; }

        public override string ToString()
        {
            return
                $"Name: {Name} Query (10 chars): {Query.Substring(0,10)}, BaseFileName: {BaseFileName}";
        }
    }

    public class QueryInfoValidator : AbstractValidator<QueryInfo>
    {
        public QueryInfoValidator()
        {
            RuleFor(target => target.Name).NotEmpty();
            RuleFor(target => target.Query).NotEmpty();
            RuleFor(target => target.BaseFileName).NotEmpty();
        }
    }


    public class MapFileMapValidator : AbstractValidator<MapFile>
    {
        public MapFileMapValidator()
        {
            RuleFor(q => q.Description).NotNull();
            RuleFor(q => q.CSVPrefix).NotNull();
            RuleFor(q => q.Author).NotEmpty();
            RuleFor(q => q.Email).NotEmpty();
            RuleFor(q => q.Id).NotEmpty();
            RuleFor(q => q.Version).NotEmpty();
            RuleFor(q => q.FileName).NotEmpty();
            RuleFor(q => q.IdentifyQuery).NotEmpty();
            RuleFor(q => q.IdentifyValue).NotEmpty();

            RuleFor(q => q.Queries.Count).GreaterThan(0).When(t => t.Queries != null);

            RuleForEach(target => target.Queries).NotNull()
                .WithMessage(
                    "Queries")
                .SetValidator(new QueryInfoValidator());
        }
    }
}
