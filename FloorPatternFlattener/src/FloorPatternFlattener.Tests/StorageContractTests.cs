using System;
using System.IO;
using System.Linq;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;
using Xunit;

namespace FloorPatternFlattener.Tests
{
    public class StorageContractTests
    {
        [Fact]
        public void EveryFieldMapsToOneSetOverload()
        {
            Assert.NotEmpty(EsFieldCatalog.Fields);
            foreach (var field in EsFieldCatalog.Fields)
            {
                var overload = EsFieldCatalog.OverloadFor(field.BuiltAs);
                Assert.NotEqual(EsSetOverload.Skip, overload);
                Assert.Equal(field.Overload, overload);
                if (field.BuiltAs == "Length")
                    Assert.Equal(EsSetOverload.LengthFeet, overload);
                else
                    Assert.Equal(EsSetOverload.NoUnit, overload);
            }
        }

        [Fact]
        public void LinkSchemaHasNoUnitOnAnyField()
        {
            var link = EsFieldCatalog.Fields.Where(f => f.SchemaName == "FPF_FlatPatternShape").ToList();
            Assert.Equal(new[] { "Version", "SourceFloorUniqueId", "ChunkIndex" }, link.Select(f => f.FieldName));
            Assert.All(link, f => Assert.Equal(EsSetOverload.NoUnit, f.Overload));
        }

        [Fact]
        public void LengthUsesFeetAndNumberDoesNot()
        {
            Assert.Equal(EsSetOverload.LengthFeet, EsFieldCatalog.OverloadFor("Length"));
            Assert.Equal(EsSetOverload.NoUnit, EsFieldCatalog.OverloadFor("Number"));
            Assert.Equal(EsSetOverload.NoUnit, EsFieldCatalog.OverloadFor("String"));
            Assert.Equal(EsSetOverload.NoUnit, EsFieldCatalog.OverloadFor("Int"));
            Assert.Equal(EsSetOverload.NoUnit, EsFieldCatalog.OverloadFor("ElementId"));
            Assert.Equal(EsSetOverload.NoUnit, EsFieldCatalog.OverloadFor("Guid"));
            Assert.Equal(EsSetOverload.Skip, EsFieldCatalog.OverloadFor(""));
            Assert.Equal(EsSetOverload.Skip, EsFieldCatalog.OverloadFor("Millimeters"));

            Assert.Equal(1.0, EsFieldCatalog.ToStoredUnits(EsSetOverload.LengthFeet, 304.8), 9);
            Assert.Equal(50.0, EsFieldCatalog.ToStoredUnits(EsSetOverload.NoUnit, 50), 9);
            Assert.Equal(50.0, EsFieldCatalog.FromStoredUnits(EsSetOverload.LengthFeet,
                EsFieldCatalog.ToStoredUnits(EsSetOverload.LengthFeet, 50)), 6);
        }

        [Fact]
        public void InstalledSharedParameterFileMatchesRevitHeader()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "FpfSharedParameters.txt");
            var text = File.ReadAllText(path);
            Assert.Null(SharedParameterFileCheck.Problem(text));
            Assert.Contains("*GROUP\tID\tNAME\r\n", text);
            Assert.Contains(SharedParameterFileCheck.ParamHeader + "\r\n", text);
            Assert.EndsWith("\t1\t0\r\n", text);
        }
    }
}
