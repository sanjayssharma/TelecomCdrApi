namespace TelecomCdr.Core.Models.DTO
{
    public class PagedResponse<T>
    {
        public int PageNumber { get; }
        public int PageSize { get; }
        public int TotalPages { get; }
        public int TotalCount { get; }
        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;
        public List<T> Data { get; } // The actual data for the current page

        public PagedResponse(List<T> items, int count, int pageNumber, int pageSize)
        {
            TotalCount = count;
            PageSize = pageSize;
            PageNumber = pageNumber;
            TotalPages = (int)Math.Ceiling(count / (double)pageSize);
            Data = items;
        }

        // Static factory method for convenience
        public static PagedResponse<T> Create(IEnumerable<T> source, int pageNumber, int pageSize)
        {
            var count = source.Count(); // Note: This iterates the source if it's not already materialized
            var items = source
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return new PagedResponse<T>(items, count, pageNumber, pageSize);
        }

        // Static factory method when count and items are already known (more efficient)
        public static PagedResponse<T> Create(List<T> items, int totalCount, int pageNumber, int pageSize)
        {
            return new PagedResponse<T>(items, totalCount, pageNumber, pageSize);
        }
    }
}
