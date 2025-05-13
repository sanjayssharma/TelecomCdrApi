using System.ComponentModel.DataAnnotations;

namespace TelecomCdr.Core.Models.DTO
{
    public class PaginationQuery
    {
        private const int MaxPageSize = 100; // Define a max page size
        private int _pageSize = 10; // Default page size

        [Range(1, int.MaxValue)]
        public int PageNumber { get; set; } = 1; // Default page number

        [Range(1, MaxPageSize)]
        public int PageSize
        {
            get => _pageSize;
            set => _pageSize = (value > MaxPageSize) ? MaxPageSize : value;
        }
    }
}
